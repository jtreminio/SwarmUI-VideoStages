"""Unit tests for the SwarmAudioLengthToFrames pure helpers (no ComfyUI / GPU required)."""

import torch

from SwarmVideoStagesNodes.audio_length_frames import aligned_frames, num_samples


def test_num_samples_reads_last_dimension() -> None:
    assert num_samples(torch.zeros(1, 2, 16)) == 16
    assert num_samples(torch.zeros(48000)) == 48000


def test_num_samples_non_tensor_or_empty_is_zero() -> None:
    assert num_samples(None) == 0
    assert num_samples([1, 2, 3]) == 0
    assert num_samples(torch.zeros(0)) == 0


def test_aligned_frames_rounds_up_to_multiple_of_eight_plus_one() -> None:
    # 1s @ 24fps = 24 raw frames (already aligned) -> +1 = 25.
    assert aligned_frames(1.0, 24) == 25
    # 1s @ 25fps = 25 raw frames -> aligns up to 32 -> +1 = 33.
    assert aligned_frames(1.0, 25) == 33
    # 0.5s @ 24fps = 12 raw frames -> aligns up to 16 -> +1 = 17.
    assert aligned_frames(0.5, 24) == 17


def test_aligned_frames_ceils_fractional_frames_before_aligning() -> None:
    # 0.1s @ 25fps = 2.5 -> ceil to 3 raw frames -> aligns up to 8 -> +1 = 9.
    assert aligned_frames(0.1, 25) == 9


def test_aligned_frames_zero_duration_is_one() -> None:
    assert aligned_frames(0.0, 24) == 1
    assert aligned_frames(-5.0, 24) == 1


def test_aligned_frames_uses_h3_grid_and_origin() -> None:
    assert aligned_frames(0.0, 24, 17, 5, 0) == 5
    assert aligned_frames(1.0, 24, 17, 5, 0) == 39
    assert aligned_frames(5.0, 24, 17, 5, 0) == 124


def test_aligned_frames_never_rounds_below_audio_duration() -> None:
    assert aligned_frames(124 / 24.0 + 0.001, 24, 17, 5, 0) == 141


def test_aligned_frames_clamps_invalid_grid_values() -> None:
    assert aligned_frames(1.0, 24, 0, 0, 0) == 24
    assert aligned_frames(1.0, 24, 17, 99, 0) == 34
