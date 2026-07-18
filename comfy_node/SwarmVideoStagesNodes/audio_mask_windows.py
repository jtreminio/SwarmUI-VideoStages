"""Pure helpers for SwarmSetAudioMaskWindows (no ComfyUI imports, unit-testable)."""

from __future__ import annotations

import json
import math

import torch


def parse_mask_windows(windows_json):
    """Parse a JSON array of ``{"start": float, "end": float}`` seconds windows.

    Malformed JSON / non-array input yields ``[]``. Windows with non-numeric or
    inverted bounds are dropped. Returns a list of (start, end) tuples.
    """
    text = (windows_json or "").strip()
    if not text:
        return []
    try:
        data = json.loads(text)
    except (ValueError, TypeError):
        return []
    if not isinstance(data, list):
        return []
    windows = []
    for item in data:
        if not isinstance(item, dict):
            continue
        try:
            start = float(item.get("start", 0))
            end = float(item.get("end", 0))
        except (TypeError, ValueError):
            continue
        if end > start >= 0:
            windows.append((start, end))
    return windows


def audio_latents_per_second(audio_vae, latent_downsample_factor) -> float:
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


def build_windowed_audio_mask(latent_shape, windows, latents_per_second, gap_mask_value):
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
