import pytest
import torch

from SwarmVideoStagesNodes.minimax_h3_audio_window import audio_latent_window


def latent(steps: int) -> torch.Tensor:
    """[1, 32, 2, steps] where step j is filled with value j."""
    return torch.arange(steps, dtype=torch.float32).view(1, 1, 1, -1).expand(1, 32, 2, -1).contiguous()


def step_values(samples: torch.Tensor) -> list[float]:
    return [float(v) for v in samples[0, 0, 0]]


def test_plain_window_inside_the_latent():
    out = audio_latent_window(latent(200), start_seconds=1.0, duration_seconds=0.5)
    assert step_values(out) == [float(i) for i in range(40, 60)]


@pytest.mark.parametrize("tail_frames", [73, 56, 39])
def test_a_tail_window_reaches_the_final_step(tail_frames):
    """Rounding start and end separately used to stop a step short of the end."""
    total_frames = 124
    total = round(total_frames / 24 * 40)
    out = audio_latent_window(
        latent(total),
        start_seconds=(total_frames - tail_frames) / 24,
        duration_seconds=tail_frames / 24,
    )
    assert step_values(out)[-1] == float(total - 1)


def test_the_boundary_case_tail_of_a_124_frame_clip():
    """73 frames at 24 fps is 3.0417s: the last 122 of a 5.1667s clip's 207 steps."""
    out = audio_latent_window(latent(207), start_seconds=51 / 24, duration_seconds=73 / 24)
    assert out.shape[-1] == 122


def test_window_past_the_end_is_clamped():
    out = audio_latent_window(latent(50), start_seconds=1.0, duration_seconds=10.0)
    assert step_values(out) == [float(i) for i in range(40, 50)]


def test_start_past_the_end_keeps_the_final_step():
    out = audio_latent_window(latent(10), start_seconds=99.0, duration_seconds=1.0)
    assert step_values(out) == [9.0]


def test_sub_step_duration_keeps_one_step():
    out = audio_latent_window(latent(10), start_seconds=0.0, duration_seconds=0.001)
    assert out.shape[-1] == 1


def test_window_is_detached_from_the_source_storage():
    """A last-dim slice is a view; holding it would pin the whole source latent."""
    out = audio_latent_window(latent(4000), start_seconds=0.25, duration_seconds=0.25)
    assert out.is_contiguous()
    assert out.untyped_storage().size() < latent(4000).untyped_storage().size()


def test_foreign_audio_latent_raises():
    with pytest.raises(ValueError, match="MiniMax H3 audio latent"):
        audio_latent_window(torch.zeros(1, 128, 47, 32), 0.0, 1.0)


def test_wrong_rank_raises():
    with pytest.raises(ValueError, match="MiniMax H3 audio latent"):
        audio_latent_window(torch.zeros(1, 24, 22, 80, 48), 0.0, 1.0)


def test_non_positive_duration_raises():
    with pytest.raises(ValueError, match="duration_seconds"):
        audio_latent_window(latent(10), 0.0, 0.0)


def test_empty_latent_raises():
    with pytest.raises(ValueError, match="empty"):
        audio_latent_window(latent(0), 0.0, 1.0)
