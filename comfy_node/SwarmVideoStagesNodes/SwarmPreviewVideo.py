from __future__ import annotations

import os
import random
import string
from fractions import Fraction

import folder_paths
from comfy_api.latest import Input, InputImpl, Types, io, ui


class SwarmPreviewVideo(io.ComfyNode):
    FILENAME_PREFIX: str = "swarm_preview_video"
    SUFFIX_LENGTH: int = 8
    DEFAULT_FPS: float = 24.0
    MIN_FPS: float = 0.01
    MAX_FPS: float = 99999.9

    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmPreviewVideo",
            display_name="Swarm Preview Video",
            category="SwarmUI/video",
            description="Build a video and display it as a preview.",
            inputs=[
                io.Image.Input("images", tooltip="The images to assemble into a video."),
                io.Float.Input("fps", default=cls.DEFAULT_FPS, min=cls.MIN_FPS, max=cls.MAX_FPS, step=1.0),
                io.Audio.Input("audio", optional=True, tooltip="Optional audio to mux into the preview."),
            ],
            is_output_node=True,
        )

    @classmethod
    def execute(cls, images: Input.Image, fps: float, audio: Input.Audio|None = None) -> io.NodeOutput:
        video = InputImpl.VideoFromComponents(
            Types.VideoComponents(images=images, audio=audio, frame_rate=Fraction(fps))
        )

        extension = Types.VideoContainer.get_extension(Types.VideoContainer.AUTO)
        random_suffix = "".join(random.choices(string.ascii_lowercase + string.digits, k=cls.SUFFIX_LENGTH))
        filename = f"{cls.FILENAME_PREFIX}_{random_suffix}.{extension}"
        filepath = os.path.join(folder_paths.get_temp_directory(), filename)
        video.save_to(filepath, format=Types.VideoContainer.AUTO, codec=Types.VideoCodec.AUTO)

        return io.NodeOutput(
            ui=ui.PreviewVideo([ui.SavedResult(filename, "", io.FolderType.temp)])
        )
