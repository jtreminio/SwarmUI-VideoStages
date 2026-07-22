import pytest
import torch

from SwarmVideoStagesNodes.frame_window import select_frame_window


def batch(frames: int) -> torch.Tensor:
    """[frames, 2, 2, 3] batch where frame j is filled with value j."""
    return torch.arange(frames, dtype=torch.float32).view(-1, 1, 1, 1).expand(-1, 2, 2, 3).contiguous()


def frame_values(images: torch.Tensor) -> list[float]:
    return [float(frame.flatten()[0]) for frame in images]


def test_plain_slice_inside_batch():
    out = select_frame_window(batch(10), start_frame=2, frame_count=4)
    assert frame_values(out) == [2.0, 3.0, 4.0, 5.0]


def test_short_tail_pads_by_repeating_last_frame():
    out = select_frame_window(batch(5), start_frame=3, frame_count=4)
    assert frame_values(out) == [3.0, 4.0, 4.0, 4.0]


def test_start_beyond_batch_clamps_to_last_frame():
    out = select_frame_window(batch(3), start_frame=9, frame_count=2)
    assert frame_values(out) == [2.0, 2.0]


def test_negative_start_clamps_to_zero():
    out = select_frame_window(batch(3), start_frame=-4, frame_count=2)
    assert frame_values(out) == [0.0, 1.0]


def test_exact_full_batch_is_identity():
    src = batch(4)
    out = select_frame_window(src, start_frame=0, frame_count=4)
    assert torch.equal(out, src)


def test_output_is_contiguous_and_exact_length():
    out = select_frame_window(batch(2), start_frame=0, frame_count=9)
    assert out.is_contiguous()
    assert out.shape[0] == 9


def test_invalid_frame_count_raises():
    with pytest.raises(ValueError):
        select_frame_window(batch(2), start_frame=0, frame_count=0)


def test_empty_batch_raises():
    with pytest.raises(ValueError):
        select_frame_window(batch(1)[0:0], start_frame=0, frame_count=1)
