"""ComfyUI node: PromptRelay temporal cross-attention prompt scheduling (LTX only).

Trimmed port of WhatDreamsCost-ComfyUI/ltx_director.py::_encode_relay. Schedules N
local prompts across the frame axis of a single LTX generation via an additive
Gaussian penalty on the text-key cross-attention, plus a global prompt that
conditions the whole clip. Latent geometry comes from the optional latent input
when connected, else from the latentFrames the windows payload declares, else
from an estimate over the segment lengths (the attention-time mask closure
recovers the true tokens-per-frame).
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING

import torch
from comfy_api.latest import io

if TYPE_CHECKING:
    from comfy.model_patcher import ModelPatcher
    from comfy.sd import CLIP

from .swarm_prompt_relay.patches import apply_patches, detect_ltx
from .swarm_prompt_relay.prompt_relay import (
    build_segments,
    convert_to_latent_lengths,
    create_mask_fn,
    distribute_segment_lengths,
    get_tokenizer_wrapper,
    map_token_indices,
    parse_windows,
    pixel_to_latent_frames,
)

log = logging.getLogger(__name__)


class SwarmPromptRelayEncode(io.ComfyNode):
    """Encode a global + per-window local prompt schedule and patch an LTX model's
    cross-attention so each frame window attends to its own local prompt."""

    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmPromptRelayEncode",
            display_name="Swarm Prompt Relay Encode",
            category="SwarmUI/Video",
            description=(
                "PromptRelay temporal cross-attention scheduling for LTX: a global prompt "
                "plus a JSON array of local prompt windows (each an object with a per-window "
                "duration in seconds and prompt), scheduled temporally across the clip. Window "
                "durations are converted to frames using the fps input."
            ),
            inputs=[
                io.Model.Input("model"),
                io.Clip.Input("clip"),
                io.String.Input(
                    "global_prompt", multiline=True, default="",
                    tooltip="Conditions the entire clip (persistent characters, scene, style).",
                ),
                io.String.Input(
                    "windows", multiline=True, default="",
                    tooltip=(
                        'JSON array of window objects [{"prompt": str, "seconds": float}, ...], '
                        "in schedule order. seconds is the window duration; it is converted to "
                        'frames using the fps input. Accepts {"latentFrames": int, "windows": '
                        "[...]} to state the clip's exact latent frame count instead of leaving "
                        "the node to estimate it."
                    ),
                ),
                io.Float.Input(
                    "fps", default=24.0, min=1.0, max=240.0, step=0.1,
                    tooltip="Frames per second, used to convert each window's seconds into a frame count.",
                ),
                io.Float.Input(
                    "epsilon", default=0.001, min=0.0001, max=0.99, step=0.0001,
                    tooltip="Penalty decay. <~0.1 gives sharp boundaries (paper default 0.001); higher softens.",
                ),
                io.Latent.Input(
                    "latent", optional=True,
                    tooltip="Optional. Connect the sampling latent for exact frame geometry.",
                ),
            ],
            outputs=[
                io.Model.Output(display_name="model"),
                io.Conditioning.Output(display_name="positive"),
            ],
        )

    @classmethod
    @torch.inference_mode()
    def execute(
        cls,
        model: ModelPatcher,
        clip: CLIP,
        global_prompt: str = "",
        windows: str = "",
        fps: float = 24.0,
        epsilon: float = 1e-3,
        latent: dict[str, torch.Tensor] | None = None,
    ) -> io.NodeOutput:
        for name, val in (("global_prompt", global_prompt), ("windows", windows)):
            if val is None:
                raise ValueError(
                    f"PromptRelay: '{name}' arrived as None. Set it to an empty string "
                    "or fix the upstream connection."
                )

        # Parallel per-window lists; empties are kept so we can fill them from the global below.
        # declared_latent_frames is the backend's authoritative latent geometry (absent for
        # hand-built graphs, which fall back to the estimate).
        locals_list, seconds_parsed, declared_latent_frames = parse_windows(windows)

        # Convert each window's duration (seconds) to a pixel-space frame count via fps. A window
        # that rounds to zero frames is kept as a single frame so it still holds a schedule slot.
        fps = fps if fps and fps > 0 else 24.0
        pixel_lengths_parsed = [
            max(1, round(sec * fps)) if sec > 0 else 0 for sec in seconds_parsed
        ]

        if not locals_list or (len(locals_list) == 1 and not locals_list[0]):
            log.info("[PromptRelay] No local segments found. Using global prompt exclusively.")
            conditioning = clip.encode_from_tokens_scheduled(clip.tokenize(global_prompt))
            return io.NodeOutput(model.clone(), conditioning)

        for i, p in enumerate(locals_list):
            if not p:
                locals_list[i] = (global_prompt or "").strip() or "video"

        temporal_stride = detect_ltx(model)

        # Any positive per-window length means explicit geometry; all-zero falls back to auto-distribute.
        pixel_lengths = pixel_lengths_parsed if any(pixel_lengths_parsed) else None

        parsed_lengths = None
        if latent is not None:
            samples = latent["samples"]
            latent_frames = samples.shape[2]
            tokens_per_frame = samples.shape[3] * samples.shape[4]
            if pixel_lengths:
                parsed_lengths = convert_to_latent_lengths(pixel_lengths, temporal_stride, latent_frames)
        elif declared_latent_frames:
            latent_frames = max(len(locals_list), declared_latent_frames)
            if pixel_lengths:
                parsed_lengths = convert_to_latent_lengths(pixel_lengths, temporal_stride, latent_frames)
            # Fallback; mask closure recovers true tokens-per-frame at attention time.
            tokens_per_frame = 1
        elif pixel_lengths:
            # No supplied geometry: estimate on LTX's own pixel->latent mapping.
            estimate = max(
                len(locals_list),
                pixel_to_latent_frames(sum(pixel_lengths), temporal_stride),
            )
            parsed_lengths = convert_to_latent_lengths(pixel_lengths, temporal_stride, estimate)
            latent_frames = max(1, sum(parsed_lengths))
            tokens_per_frame = 1
        else:
            latent_frames = max(1, len(locals_list))
            tokens_per_frame = 1

        tokenizer_wrapper = get_tokenizer_wrapper(clip)
        raw_tokenizer = tokenizer_wrapper.tokenizer
        full_prompt, token_ranges = map_token_indices(raw_tokenizer, global_prompt, locals_list)

        conditioning = clip.encode_from_tokens_scheduled(clip.tokenize(full_prompt))

        effective_lengths = distribute_segment_lengths(len(locals_list), latent_frames, parsed_lengths)

        # Left-padding tokenizers (LTX-2's Gemma pads to >=1024) put the real prompt tokens
        # at the END of the key axis; the mask must shift its 0-based token columns by
        # Lk - total_tokens or the penalty lands on padding and the relay does nothing.
        pad_left = bool(getattr(tokenizer_wrapper, "pad_left", False))
        total_tokens = len(raw_tokenizer(full_prompt)["input_ids"])

        log.info(
            "[PromptRelay] Latent: %d frames, %d tokens/frame (fallback), segments: %s, "
            "tokens: %d (pad_left=%s)",
            latent_frames, tokens_per_frame, effective_lengths, total_tokens, pad_left,
        )

        q_token_idx = build_segments(token_ranges, effective_lengths, epsilon)
        mask_fn = create_mask_fn(
            q_token_idx, tokens_per_frame, latent_frames,
            total_tokens=total_tokens, pad_left=pad_left,
        )

        patched = model.clone()
        apply_patches(patched, mask_fn)

        return io.NodeOutput(patched, conditioning)
