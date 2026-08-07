"""Pure helpers for SwarmMiniMaxH3AudioLatentWindow (importable without ComfyUI)."""

from __future__ import annotations

import torch

from .minimax_h3_latent_refs import audio_latent_length

# H3's audio VAE is 32 kHz at 800 samples per latent step; core pins the same
# rate as AUDIO_LATENT_FPS.
LATENTS_PER_SECOND = 40.0


def audio_latent_window(
    samples: torch.Tensor,
    start_seconds: float,
    duration_seconds: float,
) -> torch.Tensor:
    """A MiniMax H3 audio latent narrowed to a seconds window on its T axis.

    The window end is derived from the rounded start rather than rounded
    separately, so a tail window reaches the final step instead of stopping one
    short. A window past the end is clamped, and a start past the end keeps the
    final step, since an empty latent has no meaning downstream.
    """
    total = audio_latent_length(samples, "audio latent")
    if duration_seconds <= 0:
        raise ValueError(f"duration_seconds must be > 0, got {duration_seconds}")
    if total < 1:
        raise ValueError("audio latent is empty")
    start = max(0, min(round(start_seconds * LATENTS_PER_SECOND), total - 1))
    end = max(start + 1, round((start_seconds + duration_seconds) * LATENTS_PER_SECOND))
    # a last-dim slice is a view that would pin the whole source latent
    return samples[..., start:end].contiguous()
