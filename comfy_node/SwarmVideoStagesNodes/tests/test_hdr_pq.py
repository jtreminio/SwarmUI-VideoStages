"""Unit tests for the HDR linear->PQ Rec.2020 pure helper (no ComfyUI / GPU required)."""

import torch

from SwarmVideoStagesNodes.hdr_pq import linear709_to_pq2020


def _pq_encode(y: float) -> float:
    """Independent SMPTE ST 2084 inverse-EOTF reference (y = luminance / 10000, clamped)."""
    m1, m2 = 0.1593017578125, 78.84375
    c1, c2, c3 = 0.8359375, 18.8515625, 18.6875
    y = min(max(y, 0.0), 1.0)
    y_m1 = y**m1
    return ((c1 + c2 * y_m1) / (1.0 + c3 * y_m1)) ** m2


def _encode(rgb: list[float], diffuse_white_nits: float = 100.0) -> list[float]:
    out = linear709_to_pq2020(torch.tensor([[[rgb]]], dtype=torch.float32), diffuse_white_nits)
    return out.flatten().tolist()


def test_neutral_axis_matches_the_pq_reference() -> None:
    # 709->2020 rows sum to 1, so neutral grey is unchanged by the gamut step; only PQ applies.
    for grey in (0.0, 0.18, 0.5, 1.0, 4.0):
        for nits in (100.0, 203.0, 1000.0):
            expected = _pq_encode(grey * nits / 10000.0)
            for channel in _encode([grey, grey, grey], nits):
                assert abs(channel - expected) < 1e-5


def test_black_encodes_to_the_pq_floor() -> None:
    # PQ(0) is c1^m2 (~7e-7), not exactly 0 — negligible and rounds to code 0 in 10-bit.
    assert all(abs(c) < 1e-5 for c in _encode([0.0, 0.0, 0.0]))


def test_diffuse_white_maps_to_expected_pq_level() -> None:
    # Linear 1.0 at 100 nits -> Y = 0.01 -> PQ ~0.5081.
    assert all(abs(c - _pq_encode(0.01)) < 1e-5 for c in _encode([1.0, 1.0, 1.0], 100.0))


def test_extreme_highlights_clamp_to_one() -> None:
    assert all(abs(c - 1.0) < 1e-6 for c in _encode([1e6, 1e6, 1e6], 100.0))


def test_output_stays_in_unit_range() -> None:
    out = linear709_to_pq2020(torch.rand(2, 4, 4, 3) * 50.0, 100.0)
    assert float(out.min()) >= 0.0
    assert float(out.max()) <= 1.0


def test_negative_input_is_floored_at_zero() -> None:
    # Negative light is clamped to 0 before encoding, so it matches the black PQ floor.
    assert _encode([-5.0, -5.0, -5.0]) == _encode([0.0, 0.0, 0.0])


def test_brighter_input_encodes_higher() -> None:
    dim = _encode([0.1, 0.1, 0.1])[0]
    bright = _encode([2.0, 2.0, 2.0])[0]
    assert bright > dim


def test_higher_diffuse_white_brightens_same_input() -> None:
    low = _encode([0.5, 0.5, 0.5], 100.0)[0]
    high = _encode([0.5, 0.5, 0.5], 1000.0)[0]
    assert high > low


def test_gamut_step_desaturates_709_primary_into_rec2020() -> None:
    # Rec.709 red sits inside Rec.2020, so its 2020 coords gain small positive green/blue.
    r, gch, b = _encode([1.0, 0.0, 0.0], 100.0)
    assert r > gch > 0.0
    assert r > b > 0.0


def test_shape_and_dtype_preserved() -> None:
    out = linear709_to_pq2020(torch.rand(3, 8, 8, 3), 100.0)
    assert out.shape == (3, 8, 8, 3)
    assert out.dtype == torch.float32
