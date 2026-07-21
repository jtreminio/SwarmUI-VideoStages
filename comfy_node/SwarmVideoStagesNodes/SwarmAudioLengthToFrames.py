from __future__ import annotations

from typing import Final, NotRequired, TypedDict

import torch
from comfy_api.latest import io

from .audio_length_frames import _aligned_frames, _num_samples


class AudioPayload(TypedDict):
    waveform: torch.Tensor
    sample_rate: float | int
    path: NotRequired[str]


MIN_FRAME_RATE: Final[int] = 1
MAX_FRAME_RATE: Final[int] = 120
DEFAULT_FRAME_RATE: Final[int] = 24


class SwarmAudioLengthToFrames(io.ComfyNode):

    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmAudioLengthToFrames",
            display_name="Swarm Audio Length To Frames",
            category="SwarmUI/Audio",
            description=(
                "Compute frame count from audio duration: "
                "ceil(duration * frame_rate), aligned up to a multiple of 8, then +1."
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
