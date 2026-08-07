import pytest
import torch

from SwarmVideoStagesNodes.minimax_h3_latent_refs import (
    audio_latent_length,
    exceeds_reference_canvas,
    generated_frame_count,
    latent_frames,
    pixel_frames,
    plan_video_reference,
    reject_orphan_frames,
    qwen_sample_indices,
    qwen_timestamps,
    video_latent_dims,
)

# 5 -> 2, 22 -> 7, ... : one 17-frame encoder chunk is 5 latent frames, less a
# global 3-frame tail drop.
GRID = [(5, 2), (22, 7), (39, 12), (73, 22), (124, 37)]


def video_latent(latent_t: int, latent_h: int = 80, latent_w: int = 48, channels: int = 24) -> dict:
    return {"samples": torch.zeros(1, channels, latent_t, latent_h, latent_w)}


def audio_latent(steps: int, channels: int = 32, stereo: int = 2) -> torch.Tensor:
    return torch.zeros(1, channels, stereo, steps)


class Nested:
    """Stands in for comfy's NestedTensor, whose ndim is the max of its halves."""

    is_nested = True
    ndim = 5
    shape = (1, 24, 37, 80, 48)


# grid arithmetic


@pytest.mark.parametrize("length,expected", [(1, 5), (5, 5), (6, 22), (22, 22), (124, 124), (125, 141)])
def test_generated_frame_count_floors_then_snaps_up(length, expected):
    assert generated_frame_count(length) == expected


@pytest.mark.parametrize("frames,tokens", GRID)
def test_frames_and_latent_frames_round_trip(frames, tokens):
    assert latent_frames(frames) == tokens
    assert pixel_frames(tokens) == frames


def test_the_map_is_not_injective():
    """A whole 17-frame chunk also yields 2 tokens; pixel_frames picks the shortest."""
    assert latent_frames(17) == latent_frames(5) == 2
    assert pixel_frames(2) == 5


def test_pixel_frames_never_goes_negative():
    """An off-grid length would otherwise trim frames off the end instead of raising."""
    assert pixel_frames(1) == 5
    assert pixel_frames(0) == 5


# qwen presentation


def test_qwen_sample_indices_are_every_twelfth_frame():
    assert qwen_sample_indices(73) == [0, 12, 24, 36, 48, 60, 72]


def test_qwen_timestamps_are_half_second_steps():
    assert qwen_timestamps(4) == [0.0, 0.5, 1.0, 1.5]


# canvas budget


def test_canvas_within_the_reference_budget():
    assert not exceeds_reference_canvas(768, 1280)
    assert not exceeds_reference_canvas(384, 640)
    assert not exceeds_reference_canvas(1344, 768)


def test_canvas_past_the_short_edge_or_the_area_cap():
    assert exceeds_reference_canvas(1024, 1792)
    assert exceeds_reference_canvas(768, 1400)


# video latent validation


def test_video_latent_dims_returns_t_h_w():
    assert video_latent_dims(video_latent(22), "ref_video_latent_0") == (22, 80, 48)


@pytest.mark.parametrize("latent_t", [1, 3, 6, 21, 23])
def test_off_grid_latent_length_raises(latent_t):
    with pytest.raises(ValueError, match="5k\\+2 grid"):
        video_latent_dims(video_latent(latent_t), "ref_video_latent_0")


@pytest.mark.parametrize("latent_h,latent_w", [(81, 48), (80, 49), (81, 49)])
def test_odd_spatial_dims_raise(latent_h, latent_w):
    """LatentUpscaleBy rounds, so a scale like 0.9 on 48 lands on an odd 43."""
    with pytest.raises(ValueError, match="multiple of 32"):
        video_latent_dims(video_latent(22, latent_h, latent_w), "ref_video_latent_0")


def test_foreign_video_latent_raises_on_channels():
    """An LTX-2 video latent is 5D with an on-grid T=17; only the channel count differs."""
    with pytest.raises(ValueError, match="MiniMax H3 video latent"):
        video_latent_dims(video_latent(17, channels=128), "ref_video_latent_0")


def test_audio_latent_in_a_video_slot_raises():
    with pytest.raises(ValueError, match="MiniMax H3 video latent"):
        video_latent_dims({"samples": audio_latent(122)}, "ref_video_latent_0")


def test_joint_av_latent_names_the_split_node():
    with pytest.raises(ValueError, match="LTXVSeparateAVLatent"):
        video_latent_dims({"samples": Nested()}, "ref_video_latent_0")


# audio latent validation


def test_audio_latent_length_reads_the_time_axis():
    assert audio_latent_length(audio_latent(122), "ref_audio_latent_0") == 122


def test_video_latent_in_an_audio_slot_raises():
    with pytest.raises(ValueError, match="MiniMax H3 audio latent"):
        audio_latent_length(video_latent(22)["samples"], "ref_audio_latent_0")


def test_ltx_audio_latent_raises_rather_than_slicing_its_channel_axis():
    """LTX-2's audio latent is [B, C, F, H] -- also 4D, but time is dim 2."""
    with pytest.raises(ValueError, match="MiniMax H3 audio latent"):
        audio_latent_length(torch.zeros(1, 128, 47, 32), "ref_audio_latent_0")


# reference planning


def plan(latent_t=22, frames=..., max_latent_t=37, latent_h=80, latent_w=48):
    return plan_video_reference(
        "ref_video_latent_0",
        video_latent(latent_t, latent_h, latent_w)["samples"],
        (pixel_frames(latent_t), 1280, 768) if frames is ... else frames,
        max_latent_t,
    )


def test_plan_derives_the_canvas_and_the_2fps_presentation():
    p = plan()
    assert (p.canvas_w, p.canvas_h) == (768, 1280)
    assert p.frame_count == 73
    assert p.sample_indices == [0, 12, 24, 36, 48, 60, 72]
    assert p.timestamps == [0.0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0]
    assert not p.truncated


def test_plan_accepts_frames_larger_than_the_canvas_at_the_same_aspect():
    """The caller passes the full-res decode; only the sampled frames get resized."""
    p = plan(latent_h=40, latent_w=24, frames=(73, 1280, 768))
    assert (p.canvas_w, p.canvas_h) == (384, 640)


def test_plan_truncates_an_over_long_reference():
    p = plan(latent_t=37, max_latent_t=22, frames=(124, 1280, 768))
    assert p.truncated
    assert (p.source_latent_t, p.latent_t, p.frame_count) == (37, 22, 73)
    assert p.sample_indices[-1] == 72


def test_plan_without_companion_frames_raises():
    with pytest.raises(ValueError, match="ref_video_frames_0"):
        plan(frames=None)


def test_plan_rejects_a_frame_count_mismatch():
    with pytest.raises(ValueError, match="holds 50 frames but"):
        plan(frames=(50, 1280, 768))


def test_plan_rejects_anamorphic_frames():
    with pytest.raises(ValueError, match="stretch"):
        plan(frames=(73, 576, 1024))


def test_plan_rejects_a_canvas_past_the_reference_budget():
    with pytest.raises(ValueError, match="MiniMaxH3ReferenceToVideo"):
        plan(latent_h=112, latent_w=64, frames=(73, 1792, 1024))


def test_orphan_frames_raise_but_orphan_soundtracks_do_not():
    reject_orphan_frames({"ref_video_frames_0": object()}, {"0"})
    with pytest.raises(ValueError, match="ref_video_latent_1"):
        reject_orphan_frames({"ref_video_frames_1": object()}, {"0"})
