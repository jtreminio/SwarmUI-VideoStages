"""ComfyUI node package for SwarmUI Video Stages."""

from comfy_api.latest import ComfyExtension, io

from .SwarmAudioLengthToFrames import SwarmAudioLengthToFrames
from .SwarmPreviewVideo import SwarmPreviewVideo
from .SwarmPromptRelayEncode import SwarmPromptRelayEncode
from .SwarmSetAudioMaskWindows import SwarmSetAudioMaskWindows


class SwarmVideoStagesExtension(ComfyExtension):
    """Extension entrypoint exposing SwarmUI Video Stages nodes."""

    async def get_node_list(self) -> list[type[io.ComfyNode]]:
        return [SwarmAudioLengthToFrames, SwarmPreviewVideo, SwarmPromptRelayEncode, SwarmSetAudioMaskWindows]


async def comfy_entrypoint() -> ComfyExtension:
    """Create the extension instance for ComfyUI runtime loading."""
    return SwarmVideoStagesExtension()


NODE_CLASS_MAPPINGS = {
    "SwarmAudioLengthToFrames": SwarmAudioLengthToFrames,
    "SwarmPreviewVideo": SwarmPreviewVideo,
    "SwarmPromptRelayEncode": SwarmPromptRelayEncode,
    "SwarmSetAudioMaskWindows": SwarmSetAudioMaskWindows,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "SwarmAudioLengthToFrames": "Swarm Audio Length To Frames",
    "SwarmPreviewVideo": "Swarm Preview Video",
    "SwarmPromptRelayEncode": "Swarm Prompt Relay Encode",
    "SwarmSetAudioMaskWindows": "Swarm Set Audio Mask Windows",
}

__all__ = [
    "SwarmVideoStagesExtension",
    "comfy_entrypoint",
    "NODE_CLASS_MAPPINGS",
    "NODE_DISPLAY_NAME_MAPPINGS",
]
