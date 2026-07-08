"""ComfyUI node package for SwarmUI Video Stages."""

from comfy_api.latest import ComfyExtension, io

from .SwarmAudioLengthToFrames import SwarmAudioLengthToFrames
from .SwarmPreviewVideo import SwarmPreviewVideo
from .SwarmPromptRelayEncode import SwarmPromptRelayEncode


class SwarmVideoStagesExtension(ComfyExtension):
    """Extension entrypoint exposing SwarmUI Video Stages nodes."""

    async def get_node_list(self) -> list[type[io.ComfyNode]]:
        return [SwarmAudioLengthToFrames, SwarmPreviewVideo, SwarmPromptRelayEncode]


async def comfy_entrypoint() -> ComfyExtension:
    """Create the extension instance for ComfyUI runtime loading."""
    return SwarmVideoStagesExtension()


NODE_CLASS_MAPPINGS = {
    "SwarmAudioLengthToFrames": SwarmAudioLengthToFrames,
    "SwarmPreviewVideo": SwarmPreviewVideo,
    "SwarmPromptRelayEncode": SwarmPromptRelayEncode,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "SwarmAudioLengthToFrames": "Swarm Audio Length To Frames",
    "SwarmPreviewVideo": "Swarm Preview Video",
    "SwarmPromptRelayEncode": "Swarm Prompt Relay Encode",
}

__all__ = [
    "SwarmVideoStagesExtension",
    "comfy_entrypoint",
    "NODE_CLASS_MAPPINGS",
    "NODE_DISPLAY_NAME_MAPPINGS",
]
