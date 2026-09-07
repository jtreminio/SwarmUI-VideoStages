import type { BoundaryOut } from "./types";

export const BOUNDARY_GLYPH: Record<BoundaryOut, string> = {
    cut: "│",
    continue: "→",
    crossfade: "⤬",
};

export const BOUNDARY_LABEL: Record<BoundaryOut, string> = {
    cut: "Cut",
    continue: "Continue",
    crossfade: "Crossfade",
};
