"""Pure image-framing helpers for :class:`SwarmFrameImage`."""

from __future__ import annotations

import math

import torch
import torch.nn.functional as functional

REFERENCE_FRAMING_METHODS = ("crop", "stretch", "fit", "fit-green")


def _resize(images: torch.Tensor, width: int, height: int) -> torch.Tensor:
    channels_first = images.movedim(-1, 1)
    resized = functional.interpolate(
        channels_first,
        size=(height, width),
        mode="bicubic",
        align_corners=False,
        antialias=True,
    )
    return resized.movedim(1, -1)


def _even_down(value: float) -> int:
    return max(2, int(math.floor(value)) // 2 * 2)


def _canvas(
    images: torch.Tensor,
    width: int,
    height: int,
    green: bool,
) -> torch.Tensor:
    channels = int(images.shape[-1])
    color = images.new_zeros(channels)
    if green:
        color[0] = 0.4
        color[1] = 1.0
    if channels >= 4:
        color[3] = 1.0
    return color.view(1, 1, 1, channels).expand(
        int(images.shape[0]),
        height,
        width,
        channels,
    ).clone()


def frame_image(
    images: torch.Tensor,
    target_width: int,
    target_height: int,
    method: str,
) -> torch.Tensor:
    """Frame a channels-last ComfyUI image batch at exact target dimensions."""
    if images.ndim != 4:
        raise ValueError(
            f"images must have shape [batch, height, width, channels], got {tuple(images.shape)}"
        )
    if int(images.shape[0]) < 1:
        raise ValueError("images batch is empty")
    source_height = int(images.shape[1])
    source_width = int(images.shape[2])
    channels = int(images.shape[3])
    if source_width < 1 or source_height < 1:
        raise ValueError("source image dimensions must be positive")
    if channels < 3:
        raise ValueError("images must have at least three color channels")

    width = int(target_width)
    height = int(target_height)
    if width < 2 or height < 2:
        raise ValueError("target dimensions must be at least 2")
    normalized = str(method).strip().lower()
    if normalized not in REFERENCE_FRAMING_METHODS:
        raise ValueError(f"unsupported reference framing method: {method!r}")

    if normalized == "stretch":
        return _resize(images, width, height).contiguous()

    if normalized == "crop":
        scale = max(width / source_width, height / source_height)
        scaled_width = max(width, int(math.ceil(source_width * scale)))
        scaled_height = max(height, int(math.ceil(source_height * scale)))
        resized = _resize(images, scaled_width, scaled_height)
        left = (scaled_width - width) // 2
        top = (scaled_height - height) // 2
        return resized[:, top : top + height, left : left + width, :].contiguous()

    scale = min(width / source_width, height / source_height)
    inner_width = min(width, _even_down(source_width * scale))
    inner_height = min(height, _even_down(source_height * scale))
    resized = _resize(images, inner_width, inner_height)
    canvas = _canvas(images, width, height, green=normalized == "fit-green")
    left = (width - inner_width) // 2
    top = (height - inner_height) // 2
    canvas[:, top : top + inner_height, left : left + inner_width, :] = resized
    return canvas.contiguous()
