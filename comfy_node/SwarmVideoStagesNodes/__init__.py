"""ComfyUI node package for SwarmUI Video Stages."""

from comfy_api.latest import ComfyExtension, io

from .SwarmAudioLengthToFrames import SwarmAudioLengthToFrames


class SwarmVideoStagesExtension(ComfyExtension):
    """Extension entrypoint exposing SwarmUI Video Stages nodes."""

    async def get_node_list(self) -> list[type[io.ComfyNode]]:
        return [SwarmAudioLengthToFrames]


async def comfy_entrypoint() -> ComfyExtension:
    """Create the extension instance for ComfyUI runtime loading."""
    return SwarmVideoStagesExtension()


NODE_CLASS_MAPPINGS = {
    "SwarmAudioLengthToFrames": SwarmAudioLengthToFrames,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "SwarmAudioLengthToFrames": "Swarm Audio Length To Frames",
}

__all__ = [
    "SwarmVideoStagesExtension",
    "comfy_entrypoint",
    "NODE_CLASS_MAPPINGS",
    "NODE_DISPLAY_NAME_MAPPINGS",
]
