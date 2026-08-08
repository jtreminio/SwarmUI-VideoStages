from __future__ import annotations

from typing import Any

__all__ = [
    "NODE_CLASS_MAPPINGS",
    "NODE_DISPLAY_NAME_MAPPINGS",
]


def __getattr__(name: str) -> Any:
    if name not in __all__:
        raise AttributeError(f"module {__name__!r} has no attribute {name!r}")
    from . import extension

    value = getattr(extension, name)
    globals()[name] = value
    return value
