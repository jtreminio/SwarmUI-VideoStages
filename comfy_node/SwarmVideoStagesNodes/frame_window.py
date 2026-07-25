"""Pure helpers for SwarmFrameWindow (importable without ComfyUI)."""

from __future__ import annotations

import torch


def select_frame_window(images: torch.Tensor, start_frame: int, frame_count: int) -> torch.Tensor:
    """Exactly ``frame_count`` frames of ``images`` starting at ``start_frame``.

    The start is clamped inside the batch and a short tail is padded by
    repeating the last available frame, so the output frame count is exact by
    construction — callers plan frame math statically while the real batch
    length is only known at runtime.
    """
    if frame_count < 1:
        raise ValueError(f"frame_count must be >= 1, got {frame_count}")
    total = int(images.shape[0])
    if total < 1:
        raise ValueError("images batch is empty")
    start = max(0, min(int(start_frame), total - 1))
    window = images[start : start + frame_count]
    missing = frame_count - int(window.shape[0])
    if missing > 0:
        pad = window[-1:].expand(missing, *window.shape[1:])
        window = torch.cat([window, pad], dim=0)
    return window.contiguous()
