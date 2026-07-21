"""ComfyUI-facing wiring: node registration and the extension entrypoint.

This is the only module that ComfyUI's loader needs; the package root
re-exports it lazily so the pure helper modules stay importable without ComfyUI.
"""

from comfy_api.latest import ComfyExtension, io

from .SwarmAudioLengthToFrames import SwarmAudioLengthToFrames
from .SwarmPreviewVideo import SwarmPreviewVideo
from .SwarmPromptRelayEncode import SwarmPromptRelayEncode
from .SwarmSetAudioMaskWindows import SwarmSetAudioMaskWindows


NODES: list[tuple[type[io.ComfyNode], str]] = [
    (SwarmAudioLengthToFrames, "Swarm Audio Length To Frames"),
    (SwarmPreviewVideo, "Swarm Preview Video"),
    (SwarmPromptRelayEncode, "Swarm Prompt Relay Encode"),
    (SwarmSetAudioMaskWindows, "Swarm Set Audio Mask Windows"),
]


class SwarmVideoStagesExtension(ComfyExtension):
    """Extension entrypoint exposing SwarmUI Video Stages nodes."""

    async def get_node_list(self) -> list[type[io.ComfyNode]]:
        return [node for node, _ in NODES]


async def comfy_entrypoint() -> ComfyExtension:
    """Create the extension instance for ComfyUI runtime loading."""
    return SwarmVideoStagesExtension()


NODE_CLASS_MAPPINGS: dict[str, type[io.ComfyNode]] = {node.__name__: node for node, _ in NODES}

NODE_DISPLAY_NAME_MAPPINGS: dict[str, str] = {node.__name__: display_name for node, display_name in NODES}
