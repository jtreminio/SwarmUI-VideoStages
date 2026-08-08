"""ComfyUI-facing wiring: node registration.

This is the only module that ComfyUI's loader needs; the package root
re-exports it lazily so the pure helper modules stay importable without ComfyUI.
"""

from comfy_api.latest import io

from .SwarmAudioLengthToFrames import SwarmAudioLengthToFrames
from .SwarmFrameImage import SwarmFrameImage
from .SwarmFrameWindow import SwarmFrameWindow
from .SwarmPromptRelayEncode import SwarmPromptRelayEncode
from .SwarmRampMaskBatch import SwarmRampMaskBatch
from .SwarmSetAudioMaskWindows import SwarmSetAudioMaskWindows


NODES: list[tuple[type[io.ComfyNode], str]] = [
    (SwarmAudioLengthToFrames, "Swarm Audio Length To Frames"),
    (SwarmFrameImage, "Swarm Frame Image"),
    (SwarmFrameWindow, "Swarm Frame Window"),
    (SwarmPromptRelayEncode, "Swarm Prompt Relay Encode"),
    (SwarmRampMaskBatch, "Swarm Ramp Mask Batch"),
    (SwarmSetAudioMaskWindows, "Swarm Set Audio Mask Windows"),
]


NODE_CLASS_MAPPINGS: dict[str, type] = {node.__name__: node for node, _ in NODES}

NODE_DISPLAY_NAME_MAPPINGS: dict[str, str] = {node.__name__: display_name for node, display_name in NODES}
