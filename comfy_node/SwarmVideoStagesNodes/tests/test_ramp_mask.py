"""Unit tests for the SwarmRampMaskBatch pure helpers (no ComfyUI / GPU required)."""

import torch

from SwarmVideoStagesNodes.ramp_mask import build_ramp_mask, ramp_mask_values


def test_single_frame_blends_fifty_fifty() -> None:
    assert ramp_mask_values(1) == [0.5]


def test_two_frames_are_the_endpoints() -> None:
    assert ramp_mask_values(2) == [1.0, 0.0]


def test_ramp_runs_white_to_black_strictly_decreasing() -> None:
    values = ramp_mask_values(9)
    assert values[0] == 1.0
    assert values[-1] == 0.0
    assert all(b < a for a, b in zip(values, values[1:]))


def test_matches_the_merger_formula() -> None:
    # Mirrors MultiClipParallelMerger's per-frame value: 1 - j/(k-1).
    k = 25
    values = ramp_mask_values(k)
    for j, value in enumerate(values):
        assert abs(value - (1.0 - j / (k - 1))) < 1e-9


def test_rejects_non_positive_frame_counts() -> None:
    try:
        ramp_mask_values(0)
    except ValueError:
        pass
    else:
        raise AssertionError("expected ValueError for frames=0")


def test_build_shape_dtype_and_spatial_uniformity() -> None:
    mask = build_ramp_mask(9, width=64, height=32)
    assert mask.shape == (9, 32, 64)
    assert mask.dtype == torch.float32
    for j in range(9):
        frame = mask[j]
        assert torch.all(frame == frame[0, 0])


def test_build_frame_values_follow_the_ramp() -> None:
    mask = build_ramp_mask(5, width=8, height=8)
    for j, value in enumerate(ramp_mask_values(5)):
        assert abs(mask[j, 0, 0].item() - value) < 1e-6


def test_build_rejects_non_positive_dimensions() -> None:
    try:
        build_ramp_mask(4, width=0, height=8)
    except ValueError:
        pass
    else:
        raise AssertionError("expected ValueError for width=0")
