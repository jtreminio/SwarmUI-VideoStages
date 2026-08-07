"""ComfyUI node: slice a seconds window out of a MiniMax H3 audio latent.

This is TrimAudioDuration one stage earlier. Feeding a reference soundtrack to
H3 as a latent means the waveform is never decoded and re-encoded just to be
shortened, and core's TrimVideoLatent cannot stand in: it slices dim 2, which on
an audio latent [B, 32, 2, T] is the stereo axis.

H3's audio VAE is a fixed 800 samples per latent step at 32 kHz, so the node
pins that 40 Hz rather than taking a VAE input to rediscover it. The window is
therefore exact to the latent step, but note the encoder's causal attention:
a sliced step keeps the context of the audio that preceded it, so this is not
the same tensor as encoding the window's waveform on its own.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from comfy_api.latest import io

if TYPE_CHECKING:
    import torch

from .minimax_h3_audio_window import audio_latent_window


class SwarmMiniMaxH3AudioLatentWindow(io.ComfyNode):
    """`duration` seconds of a MiniMax H3 audio latent starting at `start`."""

    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmMiniMaxH3AudioLatentWindow",
            display_name="Swarm MiniMax H3 Audio Latent Window",
            category="SwarmUI/Audio",
            description=(
                "Narrow a MiniMax H3 audio latent to the window starting at `start` "
                "seconds and running for `duration` seconds, the latent-space "
                "equivalent of TrimAudioDuration. Core's TrimVideoLatent cannot do "
                "this: it slices dim 2, which on an audio latent is the stereo axis."
            ),
            inputs=[
                io.Latent.Input("samples", tooltip="MiniMax H3 audio latent [B, 32, 2, T]."),
                io.Float.Input(
                    "start", default=0.0, min=0.0, max=86400.0, step=0.01,
                    tooltip="Window start in seconds (clamped inside the latent).",
                ),
                io.Float.Input(
                    "duration", default=1.0, min=0.01, max=86400.0, step=0.01,
                    tooltip="Window length in seconds (clamped to what remains).",
                ),
            ],
            outputs=[io.Latent.Output(display_name="latent")],
        )

    @classmethod
    def execute(
        cls,
        samples: dict[str, torch.Tensor],
        start: float = 0.0,
        duration: float = 1.0,
    ) -> io.NodeOutput:
        out = dict(samples)
        # a mask sized for the old length cannot survive a temporal slice
        out.pop("noise_mask", None)
        out["samples"] = audio_latent_window(samples["samples"], float(start), float(duration))
        return io.NodeOutput(out)
