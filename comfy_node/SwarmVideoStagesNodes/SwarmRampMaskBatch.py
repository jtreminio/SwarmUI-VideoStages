"""ComfyUI node: a linear white->black ramp MASK batch from one node.

One-node replacement for the K x SolidMask + BatchMasks fan-in the multi-clip
merger builds for crossfade/continue seam dissolves: frame j of the output is
the uniform value 1 - j/(K-1) (a lone frame is 0.5), so a Laplacian pyramid
blend fed this mask reduces to a per-frame linear cross-dissolve.
"""

from __future__ import annotations

from comfy_api.latest import io

from .ramp_mask import build_ramp_mask


class SwarmRampMaskBatch(io.ComfyNode):
    """Batch of spatially-uniform masks ramping 1.0 -> 0.0 over `frames`."""

    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmRampMaskBatch",
            display_name="Swarm Ramp Mask Batch",
            category="SwarmUI/masks",
            description=(
                "Batch of `frames` spatially-uniform MASK frames ramping linearly "
                "from 1.0 (white, first frame) to 0.0 (black, last frame); a single "
                "frame is 0.5. Equivalent to per-frame SolidMask nodes fed into "
                "BatchMasks."
            ),
            inputs=[
                io.Int.Input(
                    "frames", default=8, min=1, max=4096,
                    tooltip="Number of mask frames in the ramp.",
                ),
                io.Int.Input(
                    "width", default=512, min=1, max=16384,
                    tooltip="Mask width in pixels.",
                ),
                io.Int.Input(
                    "height", default=512, min=1, max=16384,
                    tooltip="Mask height in pixels.",
                ),
            ],
            outputs=[
                io.Mask.Output(display_name="mask"),
            ],
        )

    @classmethod
    def execute(cls, frames: int, width: int, height: int) -> io.NodeOutput:
        return io.NodeOutput(build_ramp_mask(int(frames), int(width), int(height)))
