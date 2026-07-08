"""Unit tests for the pure PromptRelay helpers (no ComfyUI / GPU required)."""

import os
import sys

import torch

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(__file__))))

from swarm_prompt_relay.prompt_relay import (  # noqa: E402
    build_segments,
    convert_to_latent_lengths,
    distribute_segment_lengths,
    map_token_indices,
    parse_windows,
)


def test_parse_windows_returns_parallel_prompt_and_seconds_lists():
    prompts, seconds = parse_windows(
        '[{"prompt": " a red car ", "seconds": 1.5}, {"prompt": "a blue boat", "seconds": 2}]'
    )
    # Surrounding whitespace is stripped; order is preserved; fractional seconds are kept.
    assert prompts == ["a red car", "a blue boat"]
    assert seconds == [1.5, 2.0]


def test_parse_windows_keeps_lists_parallel_with_empty_prompt_and_coerces_seconds():
    prompts, seconds = parse_windows('[{"seconds": "0.5"}, {"prompt": "x", "seconds": 2}]')
    assert prompts == ["", "x"]
    assert seconds == [0.5, 2.0]


def test_parse_windows_blank_or_malformed_yields_empty():
    assert parse_windows("") == ([], [])
    assert parse_windows("   ") == ([], [])
    assert parse_windows("not json") == ([], [])
    # A JSON object (not an array) is rejected.
    assert parse_windows('{"prompt": "x", "seconds": 1}') == ([], [])


class _FakeTokenizer:
    """Whitespace tokenizer returning {'input_ids': [...]} like an HF tokenizer."""

    add_eos = False

    def __call__(self, text):
        return {"input_ids": text.split()}


def test_distribute_segment_lengths_caps_to_latent_frames():
    assert distribute_segment_lengths(2, 10, [4, 4]) == [4, 4]
    # Overshoot is clamped so the cursor never exceeds latent_frames.
    assert distribute_segment_lengths(2, 6, [4, 4]) == [4, 2]
    # Auto-distribution (ceil division).
    assert distribute_segment_lengths(3, 10) == [4, 4, 2]


def test_convert_to_latent_lengths_full_coverage_pins_to_latent_frames():
    # 40+40 = 8*10 = full coverage, so it pins to 10.
    result = convert_to_latent_lengths([40, 40], temporal_stride=8, latent_frames=10)
    assert sum(result) == 10
    assert all(v >= 1 for v in result)


def test_convert_to_latent_lengths_partial_stays_partial():
    result = convert_to_latent_lengths([8, 8], temporal_stride=8, latent_frames=100)
    assert sum(result) == 2


def test_map_token_indices_ranges_are_contiguous():
    tok = _FakeTokenizer()
    full, ranges = map_token_indices(tok, "a global", ["red car", "blue boat"])
    assert full == "a global red car blue boat"
    # 'a global' is 2 tokens, so locals start at index 2.
    assert ranges == [(2, 4), (4, 6)]


def test_build_segments_midpoints_advance_with_cursor():
    tok = _FakeTokenizer()
    _, ranges = map_token_indices(tok, "g", ["car", "boat"])
    segs = build_segments(ranges, [4, 4], epsilon=1e-3)
    assert len(segs) == 2
    assert segs[0]["midpoint"] < segs[1]["midpoint"]
    assert torch.equal(segs[0]["local_token_idx"], torch.arange(ranges[0][0], ranges[0][1]))
