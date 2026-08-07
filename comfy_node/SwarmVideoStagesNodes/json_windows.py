"""Shared parser for the SwarmUI JSON window-array inputs (no ComfyUI imports)."""

from __future__ import annotations

import json
from collections.abc import Callable
from typing import Any, TypeVar

T = TypeVar("T")


def parse_json_windows(windows_json: str | None, item_fn: Callable[[dict[str, Any]], T | None]) -> list[T]:
    """Parse a JSON array of window objects, mapping each dict through ``item_fn``.

    ``item_fn(item)`` receives one dict and returns the parsed value to keep, or
    ``None`` to drop it. Non-dict elements are always skipped. Blank / non-array /
    malformed JSON yields ``[]``. Callers own field coercion and validation.
    """
    text = (windows_json or "").strip()
    if not text:
        return []
    try:
        data = json.loads(text)
    except (ValueError, TypeError):
        return []
    if not isinstance(data, list):
        return []

    result: list[T] = []
    for item in data:
        if not isinstance(item, dict):
            continue
        parsed = item_fn(item)
        if parsed is not None:
            result.append(parsed)
    return result
