"""ComfyUI node: MiniMax H3 ref2va conditioning from already-encoded latents.

Core's MiniMaxH3ReferenceToVideo takes reference videos as pixels and encodes
them itself. When the reference is an earlier clip of the same timeline those
pixels came out of a VAE decode we already paid for, so the encode is a pure
round trip. This node takes the video latent instead, plus the small 2 fps frame
batch the Qwen3-VL tower needs (the tokenizer reads pixels; the DiT reads the
latent), and drops the encode entirely.

Reference videos that never existed as a MiniMax H3 latent -- uploads, other
architectures, decoded source footage -- have nothing to hand over, so those
still belong on core's node.

Core's second output, an empty AV latent, is not repeated here: it is just
EmptyMiniMaxH3LatentAV inlined, and VideoStages builds that latent itself. The
cost is that `length` no longer provably matches the latent being sampled, so a
truncated reference is logged rather than silently dropped.
"""

from __future__ import annotations

import logging
import math
from typing import TYPE_CHECKING, Any

import comfy.utils
import node_helpers
import nodes
from comfy.text_encoders.minimax import MiniMaxH3Tokenizer
from comfy_api.latest import io

if TYPE_CHECKING:
    import torch

from .minimax_h3_latent_refs import (
    CANVAS_MULTIPLE,
    SPATIAL_COMPRESSION,
    VIDEO_AUDIO_PREFIX,
    VIDEO_FRAMES_PREFIX,
    VIDEO_LATENT_PREFIX,
    audio_latent_length,
    generated_frame_count,
    latent_frames,
    plan_video_reference,
    reject_orphan_frames,
    suffix_of,
)

log = logging.getLogger(__name__)

REF_IMAGE_SHORT_EDGE = 2048
MAX_REF_IMAGES = 9
MAX_REF_VIDEOS = 3
MAX_REF_AUDIOS = 3


def _resize(image: torch.Tensor, width: int, height: int, crop: str) -> torch.Tensor:
    # [B, H, W, C] -> [B, height, width, 3]
    samples = image[..., :3].movedim(-1, 1)
    samples = comfy.utils.common_upscale(samples, width, height, "lanczos", crop)
    return samples.movedim(1, -1)


class SwarmMiniMaxH3LatentReferenceToVideo(io.ComfyNode):
    """ref2va conditioning whose video and audio references arrive as latents."""

    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmMiniMaxH3LatentReferenceToVideo",
            display_name="Swarm MiniMax H3 Latent Reference to Video",
            category="SwarmUI/video",
            description=(
                "<Picture i> / <Video k> / <Audio j> reference conditioning for MiniMax "
                "H3, taking reference videos and audio as latents instead of re-encoding "
                "pixels. Use the same tags when prompting. Reference videos that are not "
                "already MiniMax H3 latents belong on MiniMaxH3ReferenceToVideo."
            ),
            inputs=[
                io.Clip.Input("clip"),
                io.String.Input("prompt", multiline=True, dynamic_prompts=True),
                io.Int.Input("width", default=1344, min=32, max=nodes.MAX_RESOLUTION, step=32),
                io.Int.Input("height", default=768, min=32, max=nodes.MAX_RESOLUTION, step=32),
                io.Int.Input(
                    "length", default=124, min=5, max=3600, step=17,
                    tooltip="Frame count at 24 fps, snapped up to the model's 17k+5 grid. Must match the latent being sampled: references longer than this are trimmed to it.",
                ),
                io.Combo.Input(
                    "ref_image_size", options=["match", "max"], default="match",
                    tooltip="Reference image sizing. 'match' scales each ref (down only, keeping aspect) to the generation's pixel area; 'max' uses the reference pipeline's 2048px short edge for best identity fidelity. Reference tokens ride through every sampling step, so 'max' can be several times slower.",
                ),
                io.Vae.Input(
                    "vae", optional=True,
                    tooltip="Video VAE. Required only to encode reference images; latent references need none."),
                io.Autogrow.Input(
                    "ref_images", optional=True,
                    template=io.Autogrow.TemplatePrefix(
                        input=io.Image.Input("ref_image", tooltip="Reference image (downscaled to 2048 short edge if larger, never upscaled)"),
                        prefix="ref_image_", min=0, max=MAX_REF_IMAGES)),
                io.Autogrow.Input(
                    "ref_video_latents", optional=True,
                    template=io.Autogrow.TemplatePrefix(
                        input=io.Latent.Input("ref_video_latent", tooltip="Reference video latent, trimmed on a 17k+5 frame boundary so its length is 5k+2"),
                        prefix=VIDEO_LATENT_PREFIX, min=0, max=MAX_REF_VIDEOS)),
                io.Autogrow.Input(
                    "ref_video_frames", optional=True,
                    template=io.Autogrow.TemplatePrefix(
                        input=io.Image.Input("ref_video_frames", tooltip="Decoded frames of the same-numbered reference video latent, for the text encoder. Required alongside it, and must hold exactly the frames the latent stands for."),
                        prefix=VIDEO_FRAMES_PREFIX, min=0, max=MAX_REF_VIDEOS)),
                io.Autogrow.Input(
                    "ref_video_audio_latents", optional=True,
                    template=io.Autogrow.TemplatePrefix(
                        input=io.Latent.Input("ref_video_audio_latent", tooltip="Soundtrack latent of the same-numbered reference video"),
                        prefix=VIDEO_AUDIO_PREFIX, min=0, max=MAX_REF_VIDEOS)),
                io.Autogrow.Input(
                    "ref_audio_latents", optional=True,
                    template=io.Autogrow.TemplatePrefix(
                        input=io.Latent.Input("ref_audio_latent", tooltip="Standalone reference audio latent"),
                        prefix="ref_audio_latent_", min=0, max=MAX_REF_AUDIOS)),
            ],
            outputs=[io.Conditioning.Output(display_name="positive")],
        )

    @classmethod
    def execute(
        cls,
        clip,
        prompt: str,
        width: int,
        height: int,
        length: int,
        ref_image_size: str = "match",
        vae=None,
        ref_images: dict[str, Any] | None = None,
        ref_video_latents: dict[str, Any] | None = None,
        ref_video_frames: dict[str, Any] | None = None,
        ref_video_audio_latents: dict[str, Any] | None = None,
        ref_audio_latents: dict[str, Any] | None = None,
    ) -> io.NodeOutput:
        # every core tokenizer swallows **kwargs, so a wrong CLIP would drop the
        # references in silence and generate as if none had been wired
        if not isinstance(clip.tokenizer, MiniMaxH3Tokenizer):
            raise ValueError(
                "SwarmMiniMaxH3LatentReferenceToVideo needs the MiniMax H3 text encoder; "
                "the wired CLIP would ignore every reference."
            )
        max_latent_t = latent_frames(generated_frame_count(length))

        ref_items = []   # for the tokenizer presentation, in request order
        ref_blocks = []  # for the DiT payload, same order

        for name, img in (ref_images or {}).items():
            if img is None:
                continue
            if vae is None:
                raise ValueError(f"{name} needs the vae input to be encoded.")
            h, w = img.shape[1], img.shape[2]
            if ref_image_size == "match":
                # aspect-preserving scale (down only) to the generation's pixel area
                scale = min(1.0, math.sqrt((width * height) / (w * h)))
            else:
                scale = min(1.0, REF_IMAGE_SHORT_EDGE / min(w, h))
            tw = max(CANVAS_MULTIPLE, round(w * scale / CANVAS_MULTIPLE) * CANVAS_MULTIPLE)
            th = max(CANVAS_MULTIPLE, round(h * scale / CANVAS_MULTIPLE) * CANVAS_MULTIPLE)
            resized = _resize(img[:1], tw, th, "disabled")
            ref_items.append({"type": "image", "data": resized})
            ref_blocks.append({
                "kind": "image",
                "latent_h": th // SPATIAL_COMPRESSION,
                "latent_w": tw // SPATIAL_COMPRESSION,
                "latent": vae.encode(resized),
            })

        reject_orphan_frames(
            ref_video_frames,
            {
                suffix_of(name)
                for name, latent in (ref_video_latents or {}).items()
                if latent is not None
            },
        )

        for name, latent in (ref_video_latents or {}).items():
            if latent is None:
                continue
            frames = (ref_video_frames or {}).get(f"{VIDEO_FRAMES_PREFIX}{suffix_of(name)}")
            plan = plan_video_reference(
                name,
                latent["samples"],
                None if frames is None else tuple(frames.shape[:3]),
                max_latent_t,
            )
            if plan.truncated:
                log.info(
                    "[SwarmMiniMaxH3LatentReferenceToVideo] trimming %s from %d to %d "
                    "latent frames to fit a %d-frame generation.",
                    name, plan.source_latent_t, plan.latent_t, generated_frame_count(length),
                )
            # the trim leaves a non-contiguous view that would pin the whole parent
            # latent and re-copy it on every sampling step
            samples = latent["samples"][:1, :, :plan.latent_t].contiguous()

            audio_latent = (ref_video_audio_latents or {}).get(
                f"{VIDEO_AUDIO_PREFIX}{suffix_of(name)}")
            audio_samples = None
            ref_audio_t = 0
            if audio_latent is not None:
                audio_samples = audio_latent["samples"][:1].contiguous()
                ref_audio_t = audio_latent_length(
                    audio_samples, f"{VIDEO_AUDIO_PREFIX}{suffix_of(name)}")
                # the soundtrack gets its own <Audio j> label, emitted before <Video k>
                ref_items.append({"type": "audio"})

            # Qwen sees the video at 2 fps with timestamps, at the latent's own canvas
            ref_items.append({
                "type": "video",
                "data": _resize(
                    frames[plan.sample_indices], plan.canvas_w, plan.canvas_h, "disabled"),
                "timestamps": plan.timestamps,
            })
            ref_blocks.append({
                "kind": "video_audio" if ref_audio_t else "video",
                "latent_t": plan.latent_t,
                "latent_h": plan.latent_h,
                "latent_w": plan.latent_w,
                "ref_audio_t": ref_audio_t,
                "latent": samples,
                "audio_latent": audio_samples,
            })

        for name, latent in (ref_audio_latents or {}).items():
            if latent is None:
                continue
            samples = latent["samples"][:1]
            ref_items.append({"type": "audio"})
            ref_blocks.append({
                "kind": "audio",
                "ref_audio_t": audio_latent_length(samples, name),
                "audio_latent": samples.contiguous(),
            })

        tokens = clip.tokenize(prompt, minimax_ref_items=ref_items)
        cond = clip.encode_from_tokens_scheduled(tokens)
        if ref_blocks:
            cond = node_helpers.conditioning_set_values(cond, {"minimax_refs": ref_blocks})
        return io.NodeOutput(cond)
