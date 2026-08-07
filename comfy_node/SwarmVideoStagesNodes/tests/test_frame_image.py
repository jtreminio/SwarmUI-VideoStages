import pytest
import torch

from SwarmVideoStagesNodes.frame_image import frame_image


def test_stretch_resizes_to_exact_target():
    source = torch.rand(2, 6, 10, 3)

    output = frame_image(source, 8, 12, "stretch")

    assert output.shape == (2, 12, 8, 3)


def test_crop_fills_target_without_padding():
    source = torch.zeros(1, 4, 8, 3)
    source[:, :, :4, 0] = 1

    output = frame_image(source, 4, 4, "crop")

    assert output.shape == (1, 4, 4, 3)
    assert not torch.all(output == 0)


def test_fit_uses_black_padding_and_even_inner_dimensions():
    source = torch.ones(1, 5, 9, 3)

    output = frame_image(source, 12, 12, "fit")

    assert output.shape == (1, 12, 12, 3)
    assert torch.equal(output[:, 0], torch.zeros_like(output[:, 0]))
    non_black_rows = torch.any(output != 0, dim=(0, 2, 3)).nonzero().flatten()
    assert len(non_black_rows) % 2 == 0


def test_fit_green_uses_exact_outpainting_green():
    source = torch.zeros(1, 4, 8, 3)

    output = frame_image(source, 8, 8, "fit-green")

    expected_green = torch.tensor([0.4, 1.0, 0.0])
    assert torch.allclose(output[0, 0, 0], expected_green)
    assert torch.equal(output[0, 3:5], torch.zeros_like(output[0, 3:5]))


@pytest.mark.parametrize("method", ["crop", "stretch", "fit", "fit-green"])
def test_preserves_batch_length(method: str):
    output = frame_image(torch.rand(3, 4, 6, 3), 10, 8, method)

    assert output.shape[0] == 3
    assert output.is_contiguous()


def test_rejects_unknown_method():
    with pytest.raises(ValueError, match="unsupported"):
        frame_image(torch.rand(1, 4, 6, 3), 10, 8, "future")


def test_rejects_target_too_small_for_even_inner_dimensions():
    with pytest.raises(ValueError, match="at least 2"):
        frame_image(torch.rand(1, 4, 6, 3), 1, 8, "fit")
