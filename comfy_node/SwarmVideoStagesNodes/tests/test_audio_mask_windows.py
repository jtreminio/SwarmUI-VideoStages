"""Unit tests for the SwarmSetAudioMaskWindows pure helpers (no ComfyUI / GPU required)."""

import torch

from SwarmVideoStagesNodes.audio_mask_windows import (
    audio_latents_per_second,
    build_windowed_audio_mask,
    parse_mask_windows,
)


def test_parse_mask_windows_parses_and_drops_invalid() -> None:
    windows = parse_mask_windows(
        '[{"start": 1.0, "end": 2.5}, {"start": 5, "end": 4}, {"start": -1, "end": 2}, {"end": 3}]'
    )
    # Inverted and negative-start entries are dropped; a missing start defaults to 0.
    assert windows == [(1.0, 2.5), (0.0, 3.0)]


def test_parse_mask_windows_blank_or_malformed_yields_empty() -> None:
    assert parse_mask_windows("") == []
    assert parse_mask_windows("not json") == []
    assert parse_mask_windows('{"start": 1, "end": 2}') == []


class _FakeAutoencoder:
    sampling_rate: int = 44100
    mel_hop_length: int = 512


class _FakeVae:
    autoencoder: _FakeAutoencoder = _FakeAutoencoder()


class _FakeRawAudioVae:
    """Raw AudioVAE shape: sample_rate property, no .autoencoder / .sampling_rate."""

    sample_rate: int = 44100
    mel_hop_length: int = 512


class _FakeWrapperVae:
    first_stage_model: _FakeRawAudioVae = _FakeRawAudioVae()


def test_audio_latents_per_second_from_vae_geometry() -> None:
    lps = audio_latents_per_second(_FakeVae(), 4)
    assert abs(lps - 44100 / 512 / 4) < 1e-9


def test_audio_latents_per_second_fallback_uses_sample_rate() -> None:
    # Wrapper without the .autoencoder hack attr falls back to the raw model's sample_rate.
    lps = audio_latents_per_second(_FakeWrapperVae(), 4)
    assert abs(lps - 44100 / 512 / 4) < 1e-9
    lps = audio_latents_per_second(_FakeRawAudioVae(), 4)
    assert abs(lps - 44100 / 512 / 4) < 1e-9


def test_build_windowed_audio_mask_preserves_windows_and_regenerates_gaps() -> None:
    import math

    # 100 latent frames at ~21.5 lps ≈ 4.6s of audio.
    lps = 44100 / 512 / 4
    mask = build_windowed_audio_mask((1, 128, 100, 16), [(1.0, 2.0)], lps, 1.0)
    assert mask.shape == (1, 100, 16)
    # floor/ceil: every latent frame overlapping the window is preserved, nothing beyond.
    start_idx = math.floor(1.0 * lps)
    end_idx = math.ceil(2.0 * lps)
    assert torch.all(mask[0, start_idx:end_idx, :] == 0.0)
    assert torch.all(mask[0, :start_idx, :] == 1.0)
    assert torch.all(mask[0, end_idx:, :] == 1.0)


def test_build_windowed_audio_mask_aligned_edges_do_not_bleed() -> None:
    # With lps=20, window (1.0, 2.0) maps exactly to frames [20, 40) — no extra frame past the end.
    mask = build_windowed_audio_mask((1, 128, 60, 8), [(1.0, 2.0)], 20.0, 1.0)
    assert torch.all(mask[0, 20:40, :] == 0.0)
    assert torch.all(mask[0, 40:, :] == 1.0)
    assert torch.all(mask[0, :20, :] == 1.0)


def test_build_windowed_audio_mask_clamps_to_latent_length() -> None:
    lps = 20.0
    # Window far past the latent's end -> clamped, no crash, nothing preserved.
    mask = build_windowed_audio_mask((1, 128, 10, 8), [(100.0, 200.0)], lps, 1.0)
    assert torch.all(mask == 1.0)


def test_build_windowed_audio_mask_gap_value_zero_preserves_everything() -> None:
    mask = build_windowed_audio_mask((2, 128, 10, 8), [(0.0, 0.1)], 20.0, 0.0)
    assert torch.all(mask == 0.0)
