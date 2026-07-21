import torch

from SwarmVideoStagesNodes.swarm_prompt_relay.prompt_relay import (
    _detect_attention_mode,
    build_segments,
    convert_to_latent_lengths,
    create_mask_fn,
    distribute_segment_lengths,
    map_token_indices,
    parse_windows,
)


def test_parse_windows_returns_parallel_prompt_and_seconds_lists() -> None:
    prompts, seconds = parse_windows(
        '[{"prompt": " a red car ", "seconds": 1.5}, {"prompt": "a blue boat", "seconds": 2}]'
    )
    # Surrounding whitespace is stripped; order is preserved; fractional seconds are kept.
    assert prompts == ["a red car", "a blue boat"]
    assert seconds == [1.5, 2.0]


def test_parse_windows_keeps_lists_parallel_with_empty_prompt_and_coerces_seconds() -> None:
    prompts, seconds = parse_windows('[{"seconds": "0.5"}, {"prompt": "x", "seconds": 2}]')
    assert prompts == ["", "x"]
    assert seconds == [0.5, 2.0]


def test_parse_windows_blank_or_malformed_yields_empty() -> None:
    assert parse_windows("") == ([], [])
    assert parse_windows("   ") == ([], [])
    assert parse_windows("not json") == ([], [])
    # A JSON object (not an array) is rejected.
    assert parse_windows('{"prompt": "x", "seconds": 1}') == ([], [])


class _FakeTokenizer:
    """Whitespace tokenizer returning {'input_ids': [...]} like an HF tokenizer."""

    add_eos: bool = False

    def __call__(self, text: str) -> dict[str, list[str]]:
        return {"input_ids": text.split()}


def test_distribute_segment_lengths_caps_to_latent_frames() -> None:
    assert distribute_segment_lengths(2, 10, [4, 4]) == [4, 4]
    # Overshoot is clamped so the cursor never exceeds latent_frames.
    assert distribute_segment_lengths(2, 6, [4, 4]) == [4, 2]
    # Auto-distribution (ceil division).
    assert distribute_segment_lengths(3, 10) == [4, 4, 2]


def test_convert_to_latent_lengths_full_coverage_pins_to_latent_frames() -> None:
    # 40+40 = 8*10 = full coverage, so it pins to 10.
    result = convert_to_latent_lengths([40, 40], temporal_stride=8, latent_frames=10)
    assert sum(result) == 10
    assert all(v >= 1 for v in result)


def test_convert_to_latent_lengths_partial_stays_partial() -> None:
    result = convert_to_latent_lengths([8, 8], temporal_stride=8, latent_frames=100)
    assert sum(result) == 2


def test_map_token_indices_ranges_are_contiguous() -> None:
    tok = _FakeTokenizer()
    full, ranges = map_token_indices(tok, "a global", ["red car", "blue boat"])
    assert full == "a global red car blue boat"
    # 'a global' is 2 tokens, so locals start at index 2.
    assert ranges == [(2, 4), (4, 6)]


def test_build_segments_midpoints_advance_with_cursor() -> None:
    tok = _FakeTokenizer()
    _, ranges = map_token_indices(tok, "g", ["car", "boat"])
    segs = build_segments(ranges, [4, 4], epsilon=1e-3)
    assert len(segs) == 2
    assert segs[0]["midpoint"] < segs[1]["midpoint"]
    assert torch.equal(segs[0]["local_token_idx"], torch.arange(ranges[0][0], ranges[0][1]))


def test_detect_attention_mode_audio_branch_is_scaled() -> None:
    assert _detect_attention_mode("audio_attn2", 100, 50, 8, 1, None, 10) == ("scaled", None)


def test_detect_attention_mode_grid_sizes_drive_tokens_per_frame() -> None:
    # grid_sizes[1]*[2] = 6 tokens/frame, latent_frames=8 -> video_lq=48.
    assert _detect_attention_mode("attn2", 48, 20, 8, 1, (1, 2, 3), 10) == ("video", 6)
    # Lq != video_lq falls back to the scaled (fractional-position) penalty.
    assert _detect_attention_mode("attn2", 40, 20, 8, 1, (1, 2, 3), 10) == ("scaled", 6)


def test_detect_attention_mode_without_grid_uses_divisibility_then_fallback() -> None:
    # Lq divisible by latent_frames -> tokens/frame inferred as Lq//latent_frames.
    assert _detect_attention_mode("attn2", 48, 20, 8, 1, None, 10) == ("video", 6)
    # Non-divisible Lq -> the supplied fallback tokens/frame.
    assert _detect_attention_mode("attn2", 50, 20, 8, 7, None, 10) == ("scaled", 7)


def test_detect_attention_mode_skips_cross_modal_keys() -> None:
    # Lk equal to the video token length -> cross-modal, leave unmasked.
    assert _detect_attention_mode("attn2", 48, 48, 8, 1, (1, 2, 3), 10) is None
    # Lk shorter than the prompt token count -> not the text keys, leave unmasked.
    assert _detect_attention_mode("attn2", 48, 5, 8, 1, (1, 2, 3), 10) is None


def _penalized_columns(mask: torch.Tensor) -> set[int]:
    return set(torch.nonzero(mask < 0)[:, 1].tolist())


def test_mask_fn_left_padded_keys_shift_token_columns_to_the_end() -> None:
    # Ranges 1..3 and 3..5 within a 5-token prompt ("g" + 2x2 local tokens).
    tok = _FakeTokenizer()
    _, ranges = map_token_indices(tok, "g", ["red car", "blue boat"])
    segs = build_segments(ranges, [4, 4], epsilon=1e-3)

    mask_fn = create_mask_fn(segs, 1, latent_frames=8, total_tokens=5, pad_left=True)
    # Lk=20 keys, left-padded: real tokens occupy columns 15..19.
    mask = mask_fn(8, 20, torch.float32, "cpu", {})
    assert mask is not None
    cols = _penalized_columns(mask)
    assert cols and cols <= set(range(15 + ranges[0][0], 20))
    # Nothing lands in the pad region.
    assert not cols & set(range(15))


def test_mask_fn_right_padded_keys_keep_zero_based_columns() -> None:
    tok = _FakeTokenizer()
    _, ranges = map_token_indices(tok, "g", ["red car", "blue boat"])
    segs = build_segments(ranges, [4, 4], epsilon=1e-3)

    mask_fn = create_mask_fn(segs, 1, latent_frames=8, total_tokens=5, pad_left=False)
    mask = mask_fn(8, 20, torch.float32, "cpu", {})
    assert mask is not None
    cols = _penalized_columns(mask)
    assert cols and cols <= set(range(ranges[0][0], ranges[1][1]))
