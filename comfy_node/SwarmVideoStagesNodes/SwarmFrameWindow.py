"""ComfyUI node: slice an exact frame window out of an image batch.

Unlike core ImageFromBatch (which clamps and can return FEWER frames than
asked), this node guarantees the output length: a short tail is padded by
repeating the last frame. VideoStages uses it to conform pre-existing clip
footage to the statically planned 8n+1 frame count that the multi-clip merge
math assumes.
"""

from __future__ import annotations

from comfy_api.latest import io

from .frame_window import select_frame_window


class SwarmFrameWindow(io.ComfyNode):
    """Exactly `frame_count` frames starting at `start_frame`, padded if short."""

    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmFrameWindow",
            display_name="Swarm Frame Window",
            category="SwarmUI/video",
            description=(
                "Slice exactly `frame_count` frames from `images` starting at "
                "`start_frame`. The start is clamped inside the batch and a short "
                "tail is padded by repeating the last frame, so the output length "
                "is always exactly `frame_count` (unlike ImageFromBatch, which "
                "can return fewer)."
            ),
            inputs=[
                io.Image.Input("images", tooltip="The image batch to slice."),
                io.Int.Input(
                    "start_frame", default=0, min=0, max=1048576,
                    tooltip="First frame of the window (clamped inside the batch).",
                ),
                io.Int.Input(
                    "frame_count", default=1, min=1, max=1048576,
                    tooltip="Exact number of frames to output.",
                ),
            ],
            outputs=[
                io.Image.Output(display_name="images"),
            ],
        )

    @classmethod
    def execute(cls, images, start_frame: int, frame_count: int) -> io.NodeOutput:
        return io.NodeOutput(select_frame_window(images, int(start_frame), int(frame_count)))
