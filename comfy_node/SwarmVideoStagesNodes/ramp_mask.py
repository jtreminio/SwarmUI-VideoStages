"""Pure helpers for SwarmRampMaskBatch (importable without ComfyUI)."""

from __future__ import annotations

import torch


def ramp_mask_values(frames: int) -> list[float]:
    """Per-frame mask values of a white->black linear ramp.

    Frame j carries ``1 - j/(frames-1)`` (1.0 selects the outgoing clip, 0.0 the
    incoming one); a lone frame blends 50/50.
    """
    if frames < 1:
        raise ValueError(f"frames must be >= 1, got {frames}")
    if frames == 1:
        return [0.5]
    return [1.0 - (j / (frames - 1)) for j in range(frames)]


def build_ramp_mask(frames: int, width: int, height: int) -> torch.Tensor:
    """[frames, height, width] float32 mask batch, spatially uniform per frame."""
    if width < 1 or height < 1:
        raise ValueError(f"width/height must be >= 1, got {width}x{height}")
    values = torch.tensor(ramp_mask_values(frames), dtype=torch.float32)
    return values.view(-1, 1, 1).expand(-1, height, width).contiguous()
