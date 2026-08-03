from __future__ import annotations

import math
from collections.abc import Iterable, Sequence
from typing import Any

import torch

from .json_windows import parse_json_windows


def parse_mask_windows(windows_json: str | None) -> list[tuple[float, float]]:
    def parse_item(item: dict[str, Any]) -> tuple[float, float] | None:
        try:
            start = float(item.get("start", 0))
            end = float(item.get("end", 0))
        except (TypeError, ValueError):
            return None
        if end > start >= 0:
            return start, end
        return None

    return parse_json_windows(windows_json, parse_item)


def audio_latents_per_second(audio_vae: Any, latent_downsample_factor: int) -> float:
    """Latent frames per second of audio, from the audio VAE's mel geometry.

    Handles both the comfy VAE wrapper (``.autoencoder`` hack attr exposing
    ``sampling_rate``) and the raw AudioVAE model (``sample_rate`` property).
    """
    inner = getattr(audio_vae, "autoencoder", None)
    if inner is None:
        inner = getattr(audio_vae, "first_stage_model", audio_vae)
    rate = getattr(inner, "sampling_rate", None)
    if rate is None:
        rate = inner.sample_rate
    return rate / inner.mel_hop_length / latent_downsample_factor


def build_windowed_audio_mask(
    latent_shape: Sequence[int],
    windows: Iterable[tuple[float, float]],
    latents_per_second: float,
    gap_mask_value: float,
) -> torch.Tensor:
    """Build the 3D [B, F, H] audio noise mask: 0.0 inside windows, gap value outside.

    3D (not 4D) deliberately — the LTX Director reference notes a 4D 128-channel
    audio mask confuses the sampler into masking the video latent as well.

    Edges use floor(start)/ceil(end): every latent frame whose time span overlaps
    the window is preserved. These are PRESERVE windows, so boundary frames must
    err toward keeping segment content — round(start) could regenerate the frame
    containing the segment's first samples, and the upstream-style ``round(end)+1``
    would lock a frame of silent bed past every segment end.
    """
    batch, _channels, frames, height = latent_shape
    mask = torch.full((batch, frames, height), float(gap_mask_value), dtype=torch.float32)
    for start, end in windows:
        start_idx = max(0, math.floor(start * latents_per_second))
        end_idx = min(frames, math.ceil(end * latents_per_second))
        if end_idx > start_idx:
            mask[:, start_idx:end_idx, :] = 0.0
    return mask


def copy_audio_windows(
    source: torch.Tensor,
    target: torch.Tensor,
    windows: Iterable[tuple[float, float]],
    latents_per_second: float,
    source_start_seconds: float,
) -> torch.Tensor:
    """Copy selected source windows into a target latent using a source-time offset."""
    if source.ndim != 4 or target.ndim != 4:
        raise ValueError("Audio latent window copies require [B, C, F, H] tensors.")
    if source.shape[:2] != target.shape[:2] or source.shape[3] != target.shape[3]:
        raise ValueError(
            "Source and target audio latents must have matching batch, channel, and height dimensions."
        )

    result = target.clone()
    source_offset = max(0, math.floor(source_start_seconds * latents_per_second))
    for start, end in windows:
        target_start = max(0, math.floor(start * latents_per_second))
        target_end = min(target.shape[2], math.ceil(end * latents_per_second))
        source_start = source_offset + target_start
        count = min(target_end - target_start, source.shape[2] - source_start)
        if count > 0:
            result[:, :, target_start : target_start + count, :] = source[
                :, :, source_start : source_start + count, :
            ]
    return result
