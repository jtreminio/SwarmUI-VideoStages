"""ComfyUI node: time-windowed noise mask for a pure LTX-2 AUDIO latent.

Sets ``noise_mask`` on an audio latent so the sampler PRESERVES the audio inside
each given window (mask 0.0) and treats everything outside as ``gap_mask_value``
(default 1.0 = regenerate). This is the multi-window inverse of
``LTXVSetAudioVideoMaskByTime`` (which can only mark ONE contiguous regenerate
window): audio segments need N preserve windows with generated gaps between them.

Meant to run on the audio latent BEFORE ``LTXVConcatAVLatent`` — the mask rides
into the AV latent's NestedTensor mask, exactly like the solid-mask path SwarmUI
uses for whole-track audio injection.
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING

from comfy.ldm.lightricks.vae.audio_vae import LATENT_DOWNSAMPLE_FACTOR
from comfy_api.latest import io

if TYPE_CHECKING:
    import torch
    from comfy.sd import VAE

from .audio_mask_windows import (
    audio_latents_per_second,
    build_windowed_audio_mask,
    parse_mask_windows,
)

log = logging.getLogger(__name__)


class SwarmSetAudioMaskWindows(io.ComfyNode):
    """Preserve N seconds-windows of an audio latent; regenerate the gaps."""

    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmSetAudioMaskWindows",
            display_name="Swarm Set Audio Mask Windows",
            category="SwarmUI/Audio",
            description=(
                "Sets a noise mask on a pure AUDIO latent that preserves (0.0) each "
                'window in the JSON array [{"start": sec, "end": sec}, ...] and marks '
                "everything outside the windows with gap_mask_value (1.0 = regenerate)."
            ),
            inputs=[
                io.Latent.Input("samples", tooltip="Pure audio latent (before LTXVConcatAVLatent)."),
                io.Vae.Input("audio_vae", tooltip="LTX-2 audio VAE, for seconds -> latent-frame conversion."),
                io.String.Input(
                    "windows", multiline=True, default="",
                    tooltip='JSON array of preserve windows: [{"start": sec, "end": sec}, ...].',
                ),
                io.Float.Input(
                    "gap_mask_value", default=1.0, min=0.0, max=1.0, step=0.01,
                    tooltip="Mask value outside the windows: 1.0 regenerates gaps, 0.0 preserves them.",
                ),
            ],
            outputs=[
                io.Latent.Output(display_name="latent"),
            ],
        )

    @classmethod
    def execute(
        cls,
        samples: dict[str, torch.Tensor],
        audio_vae: VAE,
        windows: str = "",
        gap_mask_value: float = 1.0,
    ) -> io.NodeOutput:
        latent_samples = samples["samples"]
        if latent_samples.ndim != 4:
            raise ValueError(
                f"SwarmSetAudioMaskWindows expects a pure audio latent [B, C, F, H]; "
                f"got {latent_samples.ndim} dims. Apply it before LTXVConcatAVLatent."
            )
        parsed = parse_mask_windows(windows)
        lps = audio_latents_per_second(audio_vae, LATENT_DOWNSAMPLE_FACTOR)
        mask = build_windowed_audio_mask(latent_samples.shape, parsed, lps, gap_mask_value)
        log.info(
            "[SwarmSetAudioMaskWindows] %d preserve windows, %.3f latents/sec, "
            "preserved %d/%d latent frames",
            len(parsed), lps,
            int((mask[0, :, 0] == 0).sum().item()), mask.shape[1],
        )
        out = dict(samples)
        out["noise_mask"] = mask
        return io.NodeOutput(out)
