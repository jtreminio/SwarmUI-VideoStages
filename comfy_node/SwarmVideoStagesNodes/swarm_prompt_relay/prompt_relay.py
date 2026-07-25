"""Pure-tensor PromptRelay helpers (LTX temporal cross-attention scheduling).

Ported from WhatDreamsCost-ComfyUI/prompt_relay.py (the pure helpers) plus
_convert_to_latent_lengths from ltx_director.py.
"""

from __future__ import annotations

import json
import logging
import math
from collections.abc import Callable, Mapping, Sequence
from typing import Any, Protocol, TypedDict

import torch

from ..json_windows import parse_json_windows

log = logging.getLogger(__name__)


class Segment(TypedDict):
    """One local-prompt schedule segment, as built by :func:`build_segments`."""

    local_token_idx: torch.Tensor
    midpoint: int
    window: float
    sigma: float


class RawTokenizer(Protocol):
    """HF/SPiece-style tokenizer: callable returning a dict with ``input_ids``."""

    def __call__(self, text: str) -> Mapping[str, Sequence[Any]]: ...


# mask_fn(Lq, Lk, dtype, device, transformer_options) -> additive mask or None.
MaskFn = Callable[
    [int, int, torch.dtype, "torch.device | str", "dict[str, Any]"],
    "torch.Tensor | None",
]


def pixel_to_latent_frames(pixel_frames: int, temporal_stride: int) -> int:
    """LTX's pixel->latent temporal mapping: the first pixel frame owns a latent frame of its own
    and every further ``temporal_stride`` pixel frames add one.

    Mirrored in the SwarmUI backend as ``Ltx2ArchitectureModule.LatentFrameCount``; the pair is
    pinned by ``Tests/fixtures/latent-frame-cases.json``.
    """
    stride = max(1, int(temporal_stride))
    return (max(1, int(pixel_frames)) - 1) // stride + 1


def parse_windows(windows_json: str | None) -> tuple[list[str], list[float], int | None]:
    """Parse the window schedule into parallel (prompts, seconds) lists plus the latent frame count.

    The payload is either a bare JSON array of window objects, or an object
    ``{"latentFrames": int, "windows": [...]}`` — the SwarmUI backend sends the latter so the node
    gets exact latent geometry instead of estimating it. Each window is an object with a ``prompt``
    string and a ``seconds`` duration (float), in schedule order (the backend pre-sorts them).
    Blank / malformed JSON yields ``([], [], None)``. Prompts are stripped of surrounding
    whitespace; empty prompts are kept so the caller can fill them from the global prompt.
    Non-numeric durations degrade to 0.0. The two lists are always the same length, so downstream
    duration/prompt counts never disagree.
    """
    def parse_item(item: dict[str, Any]) -> tuple[str, float]:
        prompt = str(item.get("prompt", "")).strip()
        try:
            duration = float(item.get("seconds", 0))
        except (TypeError, ValueError):
            duration = 0.0
        return prompt, max(duration, 0.0)

    windows_text = windows_json
    latent_frames: int | None = None
    try:
        payload = json.loads((windows_json or "").strip() or "null")
    except (ValueError, TypeError):
        payload = None
    if isinstance(payload, dict):
        try:
            declared = int(payload.get("latentFrames", 0))
        except (TypeError, ValueError):
            declared = 0
        latent_frames = declared if declared > 0 else None
        windows_text = json.dumps(payload.get("windows", []))

    pairs = parse_json_windows(windows_text, parse_item)
    prompts = [prompt for prompt, _ in pairs]
    seconds = [duration for _, duration in pairs]
    return prompts, seconds, latent_frames


def build_temporal_cost(
    q_token_idx: Sequence[Segment],
    Lq: int,
    Lk: int,
    device: torch.device | str,
    dtype: torch.dtype,
    tokens_per_frame: int | None = None,
    latent_frames: int | None = None,
    pad_offset: int = 0,
) -> torch.Tensor:
    """Gaussian penalty matrix [Lq, Lk] for cross-attention.

    With ``tokens_per_frame`` set, query rows map to whole frames (video attn).
    With ``latent_frames`` set instead, queries get fractional frame positions
    (queries that don't map to integer frames, e.g. LTXAV audio tokens).

    pad_offset shifts token columns right by the text-key padding amount: left-padding
    tokenizers (LTX-2 Gemma pads to >=1024) place the real prompt tokens at the END of
    the key axis, so 0-based token indices must be offset by Lk - total_tokens.
    """
    offset = torch.zeros(Lq, Lk, device=device, dtype=dtype)
    if tokens_per_frame is not None:
        query_frames = (torch.arange(Lq, device=device, dtype=torch.long) // tokens_per_frame).float()
    else:
        query_frames = torch.arange(Lq, device=device, dtype=torch.float32) * latent_frames / Lq

    for seg in q_token_idx:
        local = seg["local_token_idx"].to(device=device) + pad_offset
        d = (query_frames[:, None] - seg["midpoint"]).abs()
        cost = (torch.relu(d - seg["window"]) ** 2) / (2 * seg["sigma"] ** 2)
        offset[:, local] = cost.to(offset.dtype)

    return offset


def _detect_attention_mode(
    attn_type: str,
    Lq: int,
    Lk: int,
    latent_frames: int,
    fallback_tokens_per_frame: int,
    grid_sizes: Sequence[int] | None,
    max_token_idx: int,
) -> tuple[str, int | None] | None:
    """Resolve the penalty (mode, video_tokens_per_frame) for one cross-attention call.

    Returns ``None`` when the call is cross-modal (text keys padded to a fixed length
    that differs from the video token length, or shorter than the prompt) and must be
    left unmasked. ``video_tokens_per_frame`` is ``None`` for the audio branch, where it
    is unused ("scaled" mode).
    """
    if attn_type == "audio_attn2":
        return "scaled", None

    if grid_sizes is not None:
        video_tpf = int(grid_sizes[1]) * int(grid_sizes[2])
    elif Lq % latent_frames == 0:
        video_tpf = Lq // latent_frames
    else:
        video_tpf = fallback_tokens_per_frame
    video_lq = latent_frames * video_tpf

    # Skip cross-modal attention: text keys pad to a fixed length >= max_token_idx and != video_lq
    if Lk == video_lq or Lk < max_token_idx:
        return None

    return ("video" if Lq == video_lq else "scaled"), video_tpf


def create_mask_fn(
    q_token_idx: Sequence[Segment],
    fallback_tokens_per_frame: int,
    latent_frames: int,
    total_tokens: int | None = None,
    pad_left: bool = False,
) -> MaskFn:
    """Closure: mask_fn(Lq, Lk, dtype, device, transformer_options) -> additive mask or None.

    Takes shapes/dtype/device instead of tensors so callers can compute the mask
    without first materializing q/k projections — required so PromptRelay can
    wrap an existing cross-attn forward instead of replacing it.

    total_tokens is the unpadded token count of the encoded full prompt; with a
    left-padding tokenizer (pad_left=True, e.g. LTX-2's Gemma which pads to >=1024)
    the real tokens occupy the END of the key axis, so every 0-based token index is
    shifted right by Lk - total_tokens before the penalty is written.
    """
    cache: dict[tuple[int, int, str, torch.device | str], torch.Tensor] = {}
    max_token_idx = max(int(seg["local_token_idx"].max().item()) for seg in q_token_idx) + 1

    def mask_fn(
        Lq: int,
        Lk: int,
        dtype: torch.dtype,
        device: torch.device | str,
        transformer_options: dict[str, Any],
    ) -> torch.Tensor | None:
        if Lq == Lk:
            return None

        # Only apply on conditional pass — not unconditional (negative prompt)
        cond_or_uncond = transformer_options.get("cond_or_uncond", [])
        if 1 in cond_or_uncond and 0 not in cond_or_uncond:
            return None

        grid_sizes = transformer_options.get("grid_sizes", None)
        attn_type = transformer_options.get("promptrelay_attn_type", "attn2")

        detected = _detect_attention_mode(
            attn_type, Lq, Lk, latent_frames, fallback_tokens_per_frame, grid_sizes, max_token_idx
        )
        if detected is None:
            return None
        mode, video_tpf = detected

        pad_offset = 0
        if pad_left and total_tokens is not None and Lk > total_tokens:
            pad_offset = Lk - total_tokens

        key = (Lq, Lk, mode, device)
        if key not in cache:
            if mode == "video":
                cost = build_temporal_cost(q_token_idx, Lq, Lk, device, dtype, tokens_per_frame=video_tpf, pad_offset=pad_offset)
            else:
                cost = build_temporal_cost(q_token_idx, Lq, Lk, device, dtype, latent_frames=latent_frames, pad_offset=pad_offset)
            log.info(
                "[PromptRelay] Built penalty matrix (%s): Lq=%d, Lk=%d, pad_offset=%d, nonzero=%d/%d",
                mode, Lq, Lk, pad_offset, (cost > 0).sum().item(), cost.numel(),
            )
            cache[key] = -cost

        return cache[key].to(dtype)

    return mask_fn


def build_segments(
    token_ranges: Sequence[tuple[int, int]],
    segment_lengths: Sequence[int],
    epsilon: float = 1e-3,
) -> list[Segment]:
    """Per-segment metadata for the temporal penalty."""
    # Paper uses a constant sigma regardless of segment length
    sigma = 1.0 / math.log(1.0 / epsilon) if 0 < epsilon < 1 else 0.1448

    q_token_idx: list[Segment] = []
    frame_cursor = 0

    for (tok_start, tok_end), L in zip(token_ranges, segment_lengths):
        if L <= 0:
            frame_cursor += L
            continue
        midpoint = (2 * frame_cursor + L) // 2
        q_token_idx.append({
            "local_token_idx": torch.arange(tok_start, tok_end),
            "midpoint": midpoint,
            "window": float(max(L // 2 - 2, 0)),
            "sigma": sigma,
        })
        frame_cursor += L

    return q_token_idx


def get_tokenizer_wrapper(clip: Any) -> Any:
    """Extract the SDTokenizer-style wrapper from a ComfyUI CLIP object.

    The wrapper owns the raw SPiece/HF tokenizer (``.tokenizer``) plus the padding
    config (``pad_left``/``min_length``) that determines where the real prompt
    tokens land on the cross-attention key axis.
    """
    tokenizer_wrapper = clip.tokenizer
    for attr_name in dir(tokenizer_wrapper):
        if attr_name.startswith("_"):
            continue
        inner = getattr(tokenizer_wrapper, attr_name, None)
        if inner is not None and hasattr(inner, "tokenizer"):
            return inner

    raise RuntimeError(
        f"Could not find raw tokenizer on CLIP object. "
        f"Known attributes: {[a for a in dir(tokenizer_wrapper) if not a.startswith('_')]}"
    )


def map_token_indices(
    raw_tokenizer: RawTokenizer,
    global_prompt: str,
    local_prompts: Sequence[str],
) -> tuple[str, list[tuple[int, int]]]:
    """Tokenize global + space-prefixed locals; return (full_prompt, per-local token ranges).

    Uses incremental tokenization to avoid SentencePiece context-dependency issues.
    """
    prefixed_locals = [" " + lp for lp in local_prompts]
    full_prompt = global_prompt + "".join(prefixed_locals)

    # The rest of this module already hard-assumes the dict result shape
    # (raw_tokenizer(text)["input_ids"]), so the EOS probe does too.
    has_eos = getattr(raw_tokenizer, "add_eos", False)
    if not has_eos:
        ids = raw_tokenizer("test")["input_ids"]
        eos_id = getattr(raw_tokenizer, "eos_token_id", None)
        has_eos = bool(ids) and eos_id is not None and ids[-1] == eos_id

    eos_adj = 1 if has_eos else 0

    prev_len = len(raw_tokenizer(global_prompt)["input_ids"]) - eos_adj
    token_ranges: list[tuple[int, int]] = []
    built = global_prompt

    for plp in prefixed_locals:
        built += plp
        cur_len = len(raw_tokenizer(built)["input_ids"]) - eos_adj
        if cur_len <= prev_len:
            raise ValueError(f"Local prompt produced no tokens: '{plp.strip()}'")
        token_ranges.append((prev_len, cur_len))
        prev_len = cur_len

    return full_prompt, token_ranges


def distribute_segment_lengths(
    num_segments: int,
    latent_frames: int,
    specified_lengths: Sequence[int] | None = None,
) -> list[int]:
    """Validate or auto-distribute segment frame counts, capped to fit within latent_frames."""
    if specified_lengths:
        if len(specified_lengths) != num_segments:
            raise ValueError(
                f"Number of segment_lengths ({len(specified_lengths)}) "
                f"must match number of local prompts ({num_segments})"
            )
        lengths = specified_lengths
    else:
        # ceil division
        step = -(-latent_frames // num_segments)
        lengths = [step] * num_segments

    effective: list[int] = []
    cursor = 0
    for L in lengths:
        end = min(cursor + L, latent_frames)
        effective.append(max(end - cursor, 0))
        cursor = end
    return effective


def convert_to_latent_lengths(
    pixel_lengths: Sequence[int],
    temporal_stride: int,
    latent_frames: int,
) -> list[int]:
    """Convert pixel-space segment lengths to integer latent-space lengths using the
    largest-remainder method. Targets the full `latent_frames` when the pixel sum looks
    like full coverage (within one stride of latent_frames * stride). Otherwise targets
    round(total_pixel / temporal_stride) so partial-coverage timelines stay partial.
    """
    if not pixel_lengths:
        return []
    total_pixel = sum(pixel_lengths)
    if total_pixel <= 0:
        return [1] * len(pixel_lengths)

    naive_total = max(1, round(total_pixel / temporal_stride))
    target_total = min(latent_frames, naive_total)
    # Within one frame of full → snap to full coverage
    if target_total >= latent_frames - 1:
        target_total = latent_frames

    exact = [p * target_total / total_pixel for p in pixel_lengths]
    result = [int(e) for e in exact]
    diff = target_total - sum(result)
    if diff > 0:
        order = sorted(range(len(exact)), key=lambda i: -(exact[i] - int(exact[i])))
        for k in range(diff):
            result[order[k % len(order)]] += 1

    # Ensure every segment has >= 1 latent frame
    for i in range(len(result)):
        if result[i] < 1:
            max_idx = max(range(len(result)), key=lambda j: result[j])
            if result[max_idx] > 1:
                result[max_idx] -= 1
                result[i] = 1

    return result
