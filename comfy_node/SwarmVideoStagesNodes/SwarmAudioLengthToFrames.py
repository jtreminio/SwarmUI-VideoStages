"""ComfyUI node to convert audio duration into frame count."""

from __future__ import annotations

import math
from typing import Final, NotRequired, TypedDict

import torch
from comfy_api.latest import io

class AudioPayload(TypedDict):
    """Typed subset of ComfyUI AUDIO payload used by this node."""

    waveform: torch.Tensor
    sample_rate: float | int
    path: NotRequired[str]


FRAME_ALIGNMENT: Final[int] = 8
MIN_FRAME_RATE: Final[int] = 1
MAX_FRAME_RATE: Final[int] = 120
DEFAULT_FRAME_RATE: Final[int] = 24


def _num_samples(waveform: torch.Tensor | object) -> int:
    """Return the number of audio samples in a waveform tensor."""
    if not isinstance(waveform, torch.Tensor) or waveform.numel() == 0:
        return 0

    return int(waveform.shape[-1])


def _aligned_frames(duration_sec: float, frame_rate: int) -> int:
    """Compute ceil(duration * fps), aligned to `FRAME_ALIGNMENT`, plus one."""
    raw_frames = max(1, math.ceil(duration_sec * frame_rate))
    aligned_frames = int(math.ceil(raw_frames / float(FRAME_ALIGNMENT)) * FRAME_ALIGNMENT)

    return max(1, aligned_frames + 1)


class SwarmAudioLengthToFrames(io.ComfyNode):
    """Convert an AUDIO payload length into a stable frame count."""

    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmAudioLengthToFrames",
            display_name="Swarm Audio Length To Frames",
            category="SwarmUI/Audio",
            description=(
                "Compute frame count from audio duration: "
                "ceil(duration * frame_rate), aligned to a multiple of 8, then +1."
            ),
            inputs=[
                io.Audio.Input("audio"),
                io.Int.Input(
                    "frame_rate",
                    default=DEFAULT_FRAME_RATE,
                    min=MIN_FRAME_RATE,
                    max=MAX_FRAME_RATE,
                ),
            ],
            outputs=[
                io.Audio.Output("audio"),
                io.Int.Output("frames"),
            ],
        )

    @classmethod
    @torch.inference_mode()
    def execute(cls, audio: AudioPayload | dict[str, object] | None, frame_rate: int) -> io.NodeOutput:
        if not isinstance(audio, dict):
            return io.NodeOutput(None, 1)

        waveform = audio.get("waveform")
        sample_rate = audio.get("sample_rate")
        if not isinstance(sample_rate, (int, float)) or sample_rate <= 0:
            return io.NodeOutput(audio, 1)

        sample_count = _num_samples(waveform)
        if sample_count <= 0:
            return io.NodeOutput(audio, 1)

        duration_sec = sample_count / float(sample_rate)
        frame_count = _aligned_frames(duration_sec, int(frame_rate))

        return io.NodeOutput(audio, frame_count)
