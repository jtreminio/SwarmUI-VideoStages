from __future__ import annotations

from typing import Final, NotRequired, TypedDict

import torch
from comfy_api.latest import io

from .audio_length_frames import (
    DEFAULT_FRAME_COUNT_OFFSET,
    DEFAULT_FRAME_GRID,
    DEFAULT_FRAME_GRID_ORIGIN,
    _aligned_frames,
    _num_samples,
)


class AudioPayload(TypedDict):
    waveform: torch.Tensor
    sample_rate: float | int
    path: NotRequired[str]


MIN_FRAME_RATE: Final[int] = 1
MAX_FRAME_RATE: Final[int] = 120
DEFAULT_FRAME_RATE: Final[int] = 24
MAX_FRAME_GRID: Final[int] = 1024


class SwarmAudioLengthToFrames(io.ComfyNode):

    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmAudioLengthToFrames",
            display_name="Swarm Audio Length To Frames",
            category="SwarmUI/Audio",
            description=(
                "Compute frame count from audio duration: "
                "ceil(duration * frame_rate) + frame_count_offset, aligned to "
                "frame_grid * k + frame_grid_origin."
            ),
            inputs=[
                io.Audio.Input("audio"),
                io.Int.Input(
                    "frame_rate",
                    default=DEFAULT_FRAME_RATE,
                    min=MIN_FRAME_RATE,
                    max=MAX_FRAME_RATE,
                ),
                io.Int.Input(
                    "frame_grid",
                    default=DEFAULT_FRAME_GRID,
                    min=1,
                    max=MAX_FRAME_GRID,
                ),
                io.Int.Input(
                    "frame_grid_origin",
                    default=DEFAULT_FRAME_GRID_ORIGIN,
                    min=1,
                    max=MAX_FRAME_GRID,
                ),
                io.Int.Input(
                    "frame_count_offset",
                    default=DEFAULT_FRAME_COUNT_OFFSET,
                    min=0,
                    max=MAX_FRAME_GRID,
                ),
            ],
            outputs=[
                io.Audio.Output("audio"),
                io.Int.Output("frames"),
            ],
        )

    @classmethod
    @torch.inference_mode()
    def execute(
        cls,
        audio: AudioPayload | dict[str, object] | None,
        frame_rate: int,
        frame_grid: int = DEFAULT_FRAME_GRID,
        frame_grid_origin: int = DEFAULT_FRAME_GRID_ORIGIN,
        frame_count_offset: int = DEFAULT_FRAME_COUNT_OFFSET,
    ) -> io.NodeOutput:
        minimum_frames = _aligned_frames(
            0,
            int(frame_rate),
            int(frame_grid),
            int(frame_grid_origin),
            int(frame_count_offset),
        )
        if not isinstance(audio, dict):
            return io.NodeOutput(None, minimum_frames)

        waveform = audio.get("waveform")
        sample_rate = audio.get("sample_rate")
        if not isinstance(sample_rate, (int, float)) or sample_rate <= 0:
            return io.NodeOutput(audio, minimum_frames)

        sample_count = _num_samples(waveform)
        if sample_count <= 0:
            return io.NodeOutput(audio, minimum_frames)

        duration_sec = sample_count / float(sample_rate)
        frame_count = _aligned_frames(
            duration_sec,
            int(frame_rate),
            int(frame_grid),
            int(frame_grid_origin),
            int(frame_count_offset),
        )

        return io.NodeOutput(audio, frame_count)
