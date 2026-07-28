"""ComfyUI node for clip-level reference framing."""

from __future__ import annotations

from comfy_api.latest import io

from .frame_image import REFERENCE_FRAMING_METHODS, frame_image


class SwarmFrameImage(io.ComfyNode):
    """Crop, stretch, or aspect-fit an image batch to exact dimensions."""

    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmFrameImage",
            display_name="Swarm Frame Image",
            category="SwarmUI/video",
            description=(
                "Frame an image batch at exact target dimensions. Fit preserves aspect "
                "ratio with black padding; fit-green pads with #66FF00 for outpainting "
                "IC-LoRAs."
            ),
            inputs=[
                io.Image.Input("images", tooltip="The image batch to frame."),
                io.Int.Input("width", default=512, min=2, max=16384, step=1),
                io.Int.Input("height", default=512, min=2, max=16384, step=1),
                io.Combo.Input(
                    "method",
                    options=list(REFERENCE_FRAMING_METHODS),
                    default="crop",
                ),
            ],
            outputs=[io.Image.Output(display_name="images")],
        )

    @classmethod
    def execute(
        cls,
        images,
        width: int,
        height: int,
        method: str,
    ) -> io.NodeOutput:
        return io.NodeOutput(frame_image(images, int(width), int(height), method))
