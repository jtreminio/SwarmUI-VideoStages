from __future__ import annotations

import math
from typing import Final

import torch

FRAME_ALIGNMENT: Final[int] = 8


def _num_samples(waveform: torch.Tensor | object) -> int:
    if not isinstance(waveform, torch.Tensor) or waveform.numel() == 0:
        return 0

    return int(waveform.shape[-1])


def _aligned_frames(duration_sec: float, frame_rate: int) -> int:
    raw_frames = max(0, math.ceil(duration_sec * frame_rate))
    aligned_frames = int(math.ceil(raw_frames / float(FRAME_ALIGNMENT)) * FRAME_ALIGNMENT)

    return max(1, aligned_frames + 1)
