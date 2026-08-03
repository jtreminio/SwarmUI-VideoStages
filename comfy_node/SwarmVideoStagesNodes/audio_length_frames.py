from __future__ import annotations

import math
from typing import Final

import torch

DEFAULT_FRAME_GRID: Final[int] = 8
DEFAULT_FRAME_GRID_ORIGIN: Final[int] = 1
DEFAULT_FRAME_COUNT_OFFSET: Final[int] = 1


def _num_samples(waveform: torch.Tensor | object) -> int:
    if not isinstance(waveform, torch.Tensor) or waveform.numel() == 0:
        return 0

    return int(waveform.shape[-1])


def _aligned_frames(
    duration_sec: float,
    frame_rate: int,
    frame_grid: int = DEFAULT_FRAME_GRID,
    frame_grid_origin: int = DEFAULT_FRAME_GRID_ORIGIN,
    frame_count_offset: int = DEFAULT_FRAME_COUNT_OFFSET,
) -> int:
    frame_grid = max(1, int(frame_grid))
    frame_grid_origin = min(max(1, int(frame_grid_origin)), frame_grid)
    requested_frames = max(
        0,
        math.ceil(duration_sec * frame_rate) + max(0, int(frame_count_offset)),
    )
    if requested_frames <= frame_grid_origin:
        return frame_grid_origin
    grid_steps = math.ceil(
        (requested_frames - frame_grid_origin) / float(frame_grid),
    )

    return int(grid_steps * frame_grid + frame_grid_origin)
