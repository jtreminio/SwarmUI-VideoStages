import importlib.util
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import pytest
import torch


@pytest.fixture
def node():
    # Keep the GPU/server imports out of the CPU suite; only the API value objects are doubled.
    def input_field(name, **kwargs):
        return SimpleNamespace(id=name, **kwargs)

    field_type = SimpleNamespace(Input=input_field, Output=SimpleNamespace)
    api = SimpleNamespace(io=SimpleNamespace(
        ComfyNode=object,
        Schema=SimpleNamespace,
        Latent=field_type,
        Vae=field_type,
        Int=field_type,
        NodeOutput=lambda *values: values,
    ))
    path = Path(__file__).parents[1] / "SwarmResizeLatentPixelMismatch.py"
    spec = importlib.util.spec_from_file_location("latent_pixel_mismatch_node", path)
    module = importlib.util.module_from_spec(spec)
    with patch.dict("sys.modules", {"comfy_api.latest": api}):
        spec.loader.exec_module(module)
    return module.SwarmResizeLatentPixelMismatch


@pytest.mark.parametrize("shape,scale", [((1, 4, 96, 64), 8), ((1, 128, 13, 24, 16), 32)])
def test_matching_latent_skips_resize_and_preserves_the_original(node, shape, scale):
    original = {"samples": torch.empty(shape, device="meta"), "noise_mask": object(), "metadata": object()}
    vae = SimpleNamespace(spacial_compression_decode=lambda: scale)

    assert next(field for field in node.define_schema().inputs if field.id == "resized").lazy is True
    assert node.check_lazy_status(original, vae, 512, 768) == []
    assert node.execute(original, vae, 512, 768)[0] is original
    assert node.execute(original, vae, 512, 768, resized={"samples": object()})[0] is original


@pytest.mark.parametrize("width,height", [(768, 1152), (512, 1536), (1024, 768), (2048, 3072)])
def test_mismatch_requests_and_selects_the_lazy_resize_branch(node, width, height):
    original = {"samples": torch.empty((1, 128, 13, 48, 32), device="meta")}
    resized = {"samples": torch.empty((1, 128, 13, height // 32, width // 32), device="meta")}
    vae = SimpleNamespace(spacial_compression_decode=lambda: 32)

    assert node.check_lazy_status(original, vae, width, height) == ["resized"]
    assert node.check_lazy_status(original, vae, width, height, resized=resized) == []
    assert node.execute(original, vae, width, height, resized=resized)[0] is resized


def test_mismatch_cannot_silently_return_an_unresized_latent(node):
    original = {"samples": torch.empty((1, 128, 13, 48, 32), device="meta")}
    vae = SimpleNamespace(spacial_compression_decode=lambda: 32)

    with pytest.raises(ValueError, match="resize branch"):
        node.execute(original, vae, 768, 1152)


def test_audio_latents_have_no_pixel_dimensions(node):
    original = {"samples": torch.empty((1, 8, 100), device="meta")}
    vae = SimpleNamespace(spacial_compression_decode=lambda: 32)

    with pytest.raises(ValueError, match="spatial dimensions"):
        node.check_lazy_status(original, vae, 512, 768)
