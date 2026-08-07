"""Pure helpers for the MiniMax H3 latent nodes (importable without ComfyUI).

H3's video VAE encodes in independent 17-frame chunks (5 latent frames each,
minus a global 3-frame tail drop), so a latent sliced on a chunk boundary holds
exactly what encoding those frames alone would have produced. That is what lets
a reference skip the decode/re-encode round trip -- but only while the slice
stays on the grid, so the arithmetic and the shape checks live here.

Two limits worth knowing. Only a slice's LENGTH is checkable: a mid-clip window
starting off a chunk boundary is phase-shifted and looks identical to a good
one, though a tail slice of a whole latent is aligned for free (source and slice
lengths are both 5k+2, so the offset is a multiple of 5). And the chunk identity
is a property of encoded latents; a sampled latent was never an encode of
anything, so a reference taken straight off the sampler is not the tensor core's
decode-then-re-encode path would have built -- it skips that round trip's loss.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

FRAME_GRID = 17
FRAME_GRID_ORIGIN = 5
LATENT_GRID = 5
LATENT_GRID_ORIGIN = 2
SPATIAL_COMPRESSION = 16
CANVAS_MULTIPLE = 32

VIDEO_LATENT_CHANNELS = 24
AUDIO_LATENT_CHANNELS = 32
AUDIO_LATENT_STEREO = 2

FPS = 24
QWEN_SAMPLE_STRIDE = FPS // 2

BASE_SHORT_EDGE = 768
MAX_PIXELS = 768 * 1344

ASPECT_TOLERANCE = 0.02

VIDEO_LATENT_PREFIX = "ref_video_latent_"
VIDEO_FRAMES_PREFIX = "ref_video_frames_"
VIDEO_AUDIO_PREFIX = "ref_video_audio_latent_"


def generated_frame_count(length: int) -> int:
    """Frames the generation holds: `length` floored at 5, snapped up to the 17k+5 grid."""
    frames = max(FRAME_GRID_ORIGIN, int(length))
    return frames + (FRAME_GRID_ORIGIN - frames) % FRAME_GRID


def latent_frames(frame_count: int) -> int:
    if frame_count <= FRAME_GRID_ORIGIN:
        return LATENT_GRID_ORIGIN
    return (frame_count - FRAME_GRID_ORIGIN) // FRAME_GRID * LATENT_GRID + LATENT_GRID_ORIGIN


def pixel_frames(latent_t: int) -> int:
    """Pixel frames a grid-aligned `latent_t` stands for (the VAE's upscale_ratio).

    Clamped like core's lambda: an off-grid length would otherwise go negative
    and silently trim frames off the end instead of failing.
    """
    return max(
        FRAME_GRID_ORIGIN,
        (latent_t - LATENT_GRID_ORIGIN) // LATENT_GRID * FRAME_GRID + FRAME_GRID_ORIGIN,
    )


def qwen_sample_indices(frame_count: int) -> list[int]:
    """Frame indices the tokenizer sees: every twelfth frame, i.e. 2 fps at 24 fps."""
    return list(range(0, frame_count, QWEN_SAMPLE_STRIDE))


def qwen_timestamps(sample_count: int) -> list[float]:
    return [i / 2.0 for i in range(sample_count)]


def exceeds_reference_canvas(width: int, height: int) -> bool:
    """Whether core would have downscaled a reference this size.

    Core fits every reference onto a 768-short-edge canvas under a 768*1344 area
    cap. A latent reference is used at whatever size it arrives, so anything past
    those bounds spends reference rows on every sampling step that the model was
    never trained to carry -- at 1920x1088 that is double core's count.
    """
    return min(width, height) > BASE_SHORT_EDGE or width * height > MAX_PIXELS


def _reject_nested(samples: Any, input_id: str) -> None:
    if getattr(samples, "is_nested", False):
        raise ValueError(
            f"{input_id} is a joint MiniMax H3 AV latent; split it with "
            "LTXVSeparateAVLatent and wire the matching half."
        )


def video_latent_dims(latent: dict[str, Any], input_id: str) -> tuple[int, int, int]:
    """Validated (latent_t, latent_h, latent_w) of a MiniMax H3 video latent.

    The temporal length must sit on the 5k+2 grid or it does not correspond to a
    whole number of encoder chunks, and both spatial dims must be even or the
    implied canvas is not a multiple of the model's 32px patch grid -- which the
    DiT's patchify reshape rejects outright.
    """
    samples = latent["samples"]
    _reject_nested(samples, input_id)
    if samples.ndim != 5 or samples.shape[1] != VIDEO_LATENT_CHANNELS:
        raise ValueError(
            f"{input_id} must be a MiniMax H3 video latent "
            f"[B, {VIDEO_LATENT_CHANNELS}, T, H, W]; got {tuple(samples.shape)}."
        )
    latent_t, latent_h, latent_w = (int(dim) for dim in samples.shape[2:])
    if latent_t < LATENT_GRID_ORIGIN or (latent_t - LATENT_GRID_ORIGIN) % LATENT_GRID != 0:
        raise ValueError(
            f"{input_id} has {latent_t} latent frames, which is off MiniMax H3's "
            f"{LATENT_GRID}k+{LATENT_GRID_ORIGIN} grid. Trim it on a "
            f"{FRAME_GRID}k+{FRAME_GRID_ORIGIN} frame boundary."
        )
    if latent_h % 2 or latent_w % 2:
        raise ValueError(
            f"{input_id} is {latent_w}x{latent_h} latents, which implies a "
            f"{latent_w * SPATIAL_COMPRESSION}x{latent_h * SPATIAL_COMPRESSION} canvas; "
            f"MiniMax H3 needs a multiple of {CANVAS_MULTIPLE}, so both latent dims "
            "must be even."
        )
    return latent_t, latent_h, latent_w


def suffix_of(input_id: str) -> str:
    """The autogrow slot ordinal, e.g. ref_video_latent_0 -> "0"."""
    return input_id.rsplit("_", 1)[-1]


def reject_orphan_frames(entries: dict[str, Any] | None, video_suffixes: set[str]) -> None:
    """Dropping a frame batch drops the whole reference from the text encoder's view.

    An orphaned soundtrack is core's own silently-ignore case and stays silent.
    """
    for name, value in (entries or {}).items():
        if value is not None and suffix_of(name) not in video_suffixes:
            raise ValueError(
                f"{name} has no {VIDEO_LATENT_PREFIX}{suffix_of(name)} to belong to.")


@dataclass(frozen=True)
class VideoReferencePlan:
    """Everything the node needs to shape one reference, decided before touching tensors."""

    latent_t: int
    source_latent_t: int
    latent_h: int
    latent_w: int
    canvas_w: int
    canvas_h: int
    frame_count: int
    sample_indices: list[int]
    timestamps: list[float]

    @property
    def truncated(self) -> bool:
        return self.latent_t < self.source_latent_t


def plan_video_reference(
    input_id: str,
    latent_samples: Any,
    frame_shape: tuple[int, int, int] | None,
    max_latent_t: int,
) -> VideoReferencePlan:
    """Validate one reference video latent against its companion frames.

    `frame_shape` is the companion batch's (count, height, width), or None when
    it was not wired. Everything here fails loudly rather than letting a
    misaligned pair through: a reference whose frames do not match its latent
    conditions the model on one window while describing another.
    """
    latent_t, latent_h, latent_w = video_latent_dims({"samples": latent_samples}, input_id)
    canvas_w = latent_w * SPATIAL_COMPRESSION
    canvas_h = latent_h * SPATIAL_COMPRESSION
    if exceeds_reference_canvas(canvas_w, canvas_h):
        raise ValueError(
            f"{input_id} is {canvas_w}x{canvas_h}, past MiniMax H3's {BASE_SHORT_EDGE}"
            "-short-edge reference canvas. Downscale the latent, or send this reference "
            "through MiniMaxH3ReferenceToVideo, which resizes pixels itself."
        )

    frames_id = f"{VIDEO_FRAMES_PREFIX}{suffix_of(input_id)}"
    if frame_shape is None:
        raise ValueError(
            f"{input_id} has no {frames_id}; the text encoder reads the reference as "
            "pixels, so the decoded frames must come with the latent."
        )
    count, frame_h, frame_w = (int(dim) for dim in frame_shape)
    expected = pixel_frames(latent_t)
    if count != expected:
        raise ValueError(
            f"{frames_id} holds {count} frames but {input_id} stands for {expected}; "
            "they must cover the same window."
        )
    if abs((frame_w / frame_h) / (canvas_w / canvas_h) - 1) > ASPECT_TOLERANCE:
        raise ValueError(
            f"{frames_id} is {frame_w}x{frame_h} but {input_id} implies "
            f"{canvas_w}x{canvas_h}; fitting them would stretch the frames the text "
            "encoder reads."
        )

    source_latent_t = latent_t
    if latent_t > max_latent_t:
        latent_t = max_latent_t
        count = pixel_frames(max_latent_t)
    sample_indices = qwen_sample_indices(count)
    return VideoReferencePlan(
        latent_t=latent_t,
        source_latent_t=source_latent_t,
        latent_h=latent_h,
        latent_w=latent_w,
        canvas_w=canvas_w,
        canvas_h=canvas_h,
        frame_count=count,
        sample_indices=sample_indices,
        timestamps=qwen_timestamps(len(sample_indices)),
    )


def audio_latent_length(latent_samples: Any, input_id: str) -> int:
    """Validated latent length of a MiniMax H3 audio latent.

    LTX-2's audio latent is also 4D but carries time on dim 2, so a rank check
    alone would slice its channel axis and silently corrupt the window.
    """
    _reject_nested(latent_samples, input_id)
    if (
        latent_samples.ndim != 4
        or latent_samples.shape[1] != AUDIO_LATENT_CHANNELS
        or latent_samples.shape[2] != AUDIO_LATENT_STEREO
    ):
        raise ValueError(
            f"{input_id} must be a MiniMax H3 audio latent "
            f"[B, {AUDIO_LATENT_CHANNELS}, {AUDIO_LATENT_STEREO}, T]; "
            f"got {tuple(latent_samples.shape)}."
        )
    return int(latent_samples.shape[-1])
