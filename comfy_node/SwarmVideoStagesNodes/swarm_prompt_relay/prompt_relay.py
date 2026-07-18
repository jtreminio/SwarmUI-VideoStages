"""Pure-tensor PromptRelay helpers (LTX temporal cross-attention scheduling).

Ported from WhatDreamsCost-ComfyUI/prompt_relay.py (the pure helpers) plus
_convert_to_latent_lengths from ltx_director.py.
"""

import json
import logging
import math

import torch

log = logging.getLogger(__name__)


def parse_windows(windows_json):
    """Parse the JSON array of window objects into parallel (prompts, seconds) lists.

    Each element is an object with a ``prompt`` string and a ``seconds`` duration (float), in
    schedule order (the SwarmUI backend pre-sorts them). Blank / non-array / malformed JSON yields
    ``([], [])``. Prompts are stripped of surrounding whitespace; empty prompts are kept so the
    caller can fill them from the global prompt. Non-numeric durations degrade to 0.0. The two lists
    are always the same length, so downstream duration/prompt counts never disagree.
    """
    text = (windows_json or "").strip()
    if not text:
        return [], []
    try:
        data = json.loads(text)
    except (ValueError, TypeError):
        return [], []
    if not isinstance(data, list):
        return [], []

    prompts = []
    seconds = []
    for item in data:
        if not isinstance(item, dict):
            continue
        prompts.append(str(item.get("prompt", "")).strip())
        try:
            duration = float(item.get("seconds", 0))
        except (TypeError, ValueError):
            duration = 0.0
        seconds.append(max(duration, 0.0))
    return prompts, seconds


def build_temporal_cost(q_token_idx, Lq, Lk, device, dtype, tokens_per_frame, pad_offset=0):
    """Gaussian penalty matrix [Lq, Lk] for video cross-attention (integer frame indexing).

    pad_offset shifts token columns right by the text-key padding amount: left-padding
    tokenizers (LTX-2 Gemma pads to >=1024) place the real prompt tokens at the END of
    the key axis, so 0-based token indices must be offset by Lk - total_tokens.
    """
    offset = torch.zeros(Lq, Lk, device=device, dtype=dtype)
    query_frames = torch.arange(Lq, device=device, dtype=torch.long) // tokens_per_frame

    for seg in q_token_idx:
        local = seg["local_token_idx"].to(device=device) + pad_offset
        d = (query_frames.float()[:, None] - seg["midpoint"]).abs()
        strength = seg.get("strength", 1.0)
        cost = strength * (torch.relu(d - seg["window"]) ** 2) / (2 * seg["sigma"] ** 2)
        offset[:, local] = cost.to(offset.dtype)

    return offset


def build_temporal_cost_scaled(q_token_idx, Lq, Lk, device, dtype, latent_frames, is_audio=False, pad_offset=0):
    """Penalty matrix for queries that don't map to integer frames (e.g. LTXAV audio tokens)."""
    offset = torch.zeros(Lq, Lk, device=device, dtype=dtype)
    query_frames = torch.arange(Lq, device=device, dtype=torch.float32) * latent_frames / Lq

    for seg in q_token_idx:
        local = seg["local_token_idx"].to(device=device) + pad_offset
        d = (query_frames[:, None] - seg["midpoint"]).abs()
        if is_audio:
            sigma_val = seg.get("sigma_audio", seg["sigma"])
            window_val = seg.get("window_audio", seg["window"])
            strength_val = seg.get("strength_audio", 1.0)
        else:
            sigma_val = seg["sigma"]
            window_val = seg["window"]
            strength_val = seg.get("strength", 1.0)
        cost = strength_val * (torch.relu(d - window_val) ** 2) / (2 * sigma_val ** 2)
        offset[:, local] = cost.to(offset.dtype)

    return offset


def create_mask_fn(q_token_idx, fallback_tokens_per_frame, latent_frames, total_tokens=None, pad_left=False):
    """Closure: mask_fn(Lq, Lk, dtype, device, transformer_options) -> additive mask or None.

    Takes shapes/dtype/device instead of tensors so callers can compute the mask
    without first materializing q/k projections — required so PromptRelay can
    wrap an existing cross-attn forward instead of replacing it.

    total_tokens is the unpadded token count of the encoded full prompt; with a
    left-padding tokenizer (pad_left=True, e.g. LTX-2's Gemma which pads to >=1024)
    the real tokens occupy the END of the key axis, so every 0-based token index is
    shifted right by Lk - total_tokens before the penalty is written.
    """
    cache = {}
    max_token_idx = max(int(seg["local_token_idx"].max().item()) for seg in q_token_idx) + 1

    def mask_fn(Lq, Lk, dtype, device, transformer_options):
        if Lq == Lk:
            return None

        # Only apply on conditional pass — not unconditional (negative prompt)
        cond_or_uncond = transformer_options.get("cond_or_uncond", [])
        if 1 in cond_or_uncond and 0 not in cond_or_uncond:
            return None

        grid_sizes = transformer_options.get("grid_sizes", None)
        attn_type = transformer_options.get("promptrelay_attn_type", "attn2")
        is_audio = (attn_type == "audio_attn2")

        if is_audio:
            mode = "scaled"
            video_tpf = fallback_tokens_per_frame
            video_lq = -1
        else:
            if grid_sizes is not None:
                video_tpf = int(grid_sizes[1]) * int(grid_sizes[2])
            else:
                if Lq % latent_frames == 0:
                    video_tpf = Lq // latent_frames
                else:
                    video_tpf = fallback_tokens_per_frame
            video_lq = latent_frames * video_tpf

            # Skip cross-modal attention: text keys pad to a fixed length >= max_token_idx and != video_lq
            if Lk == video_lq or Lk < max_token_idx:
                return None

            mode = "video" if Lq == video_lq else "scaled"

        pad_offset = 0
        if pad_left and total_tokens is not None and Lk > total_tokens:
            pad_offset = Lk - total_tokens

        key = (Lq, Lk, mode, device)
        if key not in cache:
            if mode == "video":
                cost = build_temporal_cost(q_token_idx, Lq, Lk, device, dtype, video_tpf, pad_offset=pad_offset)
            else:
                cost = build_temporal_cost_scaled(q_token_idx, Lq, Lk, device, dtype, latent_frames, is_audio=is_audio, pad_offset=pad_offset)
            log.info(
                "[PromptRelay] Built penalty matrix (%s): Lq=%d, Lk=%d, pad_offset=%d, nonzero=%d/%d",
                mode, Lq, Lk, pad_offset, (cost > 0).sum().item(), cost.numel(),
            )
            cache[key] = -cost

        return cache[key].to(dtype)

    return mask_fn


def build_segments(token_ranges, segment_lengths, epsilon=1e-3, relay_options=None):
    """Per-segment metadata for the temporal penalty.

    relay_options (optional dict) overrides per-stream knobs:
        video_strength, video_window_scale,
        audio_epsilon, audio_strength, audio_window_scale
    Audio knobs only affect architectures whose cross-attention takes the scaled
    (non-integer-frame) path — currently LTX audio_attn2.
    """
    # Paper uses a constant sigma regardless of segment length
    sigma = 1.0 / math.log(1.0 / epsilon) if 0 < epsilon < 1 else 0.1448

    opts = relay_options or {}
    v_strength = opts.get("video_strength", 1.0)
    v_window_scale = opts.get("video_window_scale", 1.0)
    a_epsilon = opts.get("audio_epsilon")
    a_strength = opts.get("audio_strength", 1.0)
    a_window_scale = opts.get("audio_window_scale", 1.0)

    if a_epsilon is not None and 0 < a_epsilon < 1:
        sigma_audio = 1.0 / math.log(1.0 / a_epsilon)
    else:
        sigma_audio = sigma

    q_token_idx = []
    frame_cursor = 0

    for (tok_start, tok_end), L in zip(token_ranges, segment_lengths):
        if L <= 0:
            frame_cursor += L
            continue
        midpoint = (2 * frame_cursor + L) // 2
        base_window = max(L // 2 - 2, 0)
        q_token_idx.append({
            "local_token_idx": torch.arange(tok_start, tok_end),
            "midpoint": midpoint,
            "window": max(base_window * v_window_scale, 0.0),
            "sigma": sigma,
            "strength": v_strength,
            "window_audio": max(base_window * a_window_scale, 0.0),
            "sigma_audio": sigma_audio,
            "strength_audio": a_strength,
        })
        frame_cursor += L

    return q_token_idx


def get_tokenizer_wrapper(clip):
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


def get_raw_tokenizer(clip):
    """Extract the raw SPiece/HF tokenizer from a ComfyUI CLIP object."""
    return get_tokenizer_wrapper(clip).tokenizer


def map_token_indices(raw_tokenizer, global_prompt, local_prompts):
    """Tokenize global + space-prefixed locals; return (full_prompt, per-local token ranges).

    Uses incremental tokenization to avoid SentencePiece context-dependency issues.
    """
    prefixed_locals = [" " + lp for lp in local_prompts]
    full_prompt = global_prompt + "".join(prefixed_locals)

    has_eos = getattr(raw_tokenizer, "add_eos", False)
    if not has_eos:
        try:
            test_res = raw_tokenizer("test")
            if isinstance(test_res, dict) and "input_ids" in test_res:
                ids = test_res["input_ids"]
            elif hasattr(test_res, "input_ids"):
                ids = test_res.input_ids
            elif isinstance(test_res, list):
                ids = test_res
            else:
                ids = []

            if ids:
                eos_id = getattr(raw_tokenizer, "eos_token_id", None)
                if eos_id is not None and ids[-1] == eos_id:
                    has_eos = True
                elif ids[-1] == 1:
                    has_eos = True
        except Exception:
            pass

    eos_adj = 1 if has_eos else 0

    prev_len = len(raw_tokenizer(global_prompt)["input_ids"]) - eos_adj
    token_ranges = []
    built = global_prompt

    for plp in prefixed_locals:
        built += plp
        cur_len = len(raw_tokenizer(built)["input_ids"]) - eos_adj
        if cur_len <= prev_len:
            raise ValueError(f"Local prompt produced no tokens: '{plp.strip()}'")
        token_ranges.append((prev_len, cur_len))
        prev_len = cur_len

    return full_prompt, token_ranges


def distribute_segment_lengths(num_segments, latent_frames, specified_lengths=None):
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

    effective = []
    cursor = 0
    for L in lengths:
        end = min(cursor + L, latent_frames)
        effective.append(max(end - cursor, 0))
        cursor = end
    return effective


def convert_to_latent_lengths(pixel_lengths, temporal_stride, latent_frames):
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
