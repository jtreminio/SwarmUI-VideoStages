import { getClips, saveClips } from "./persistence";
import { framesForClip } from "./renderUtils";
import { bindClickSelectableTrack } from "./trackDomUtils";
import type { BoundaryOut, Clip } from "./types";
import { setSelection } from "./uiState";

const CHIP_SELECTOR = "[data-vst-boundary-cycle]";

const CYCLE: BoundaryOut[] = ["cut", "continue", "crossfade"];

// Mirrors MultiClipParallelMerger.DefaultCrossfadeOverlapFrames.
export const DEFAULT_CROSSFADE_OVERLAP_FRAMES = 8;

export const nextBoundary = (current: BoundaryOut): BoundaryOut => {
    const idx = CYCLE.indexOf(current);
    return CYCLE[(idx + 1) % CYCLE.length];
};

export interface BoundaryPlan {
    /** Overlap frames removed at each interior boundary i (between clip i and i+1). */
    overlaps: number[];
    /** True when the whole plan degraded to a hard cut (a clip is too short for the overlaps). */
    fallback: boolean;
}

/**
 * Frontend mirror of the backend `ResolveCrossfadePlan` overlap math (crossfades share one clamped
 * window K, a "continue" boundary is a fixed 1-frame overlap). On this branch dims/fps are timeline-
 * uniform, so the only fallback the frontend can detect is a clip too short to fund its overlaps; the
 * LTX-2 model-family gate is enforced by the backend (authoritative) and is not checked here.
 */
export const crossfadePlanForClips = (
    clips: Clip[],
    fps: number,
): BoundaryPlan => {
    const count = clips.length;
    const boundaryCount = Math.max(0, count - 1);
    const noOverlap = (): number[] => new Array(boundaryCount).fill(0);
    if (count < 2) {
        return { overlaps: noOverlap(), fallback: false };
    }
    const crossfade: boolean[] = [];
    const cont: boolean[] = [];
    let requested = 0;
    for (let i = 0; i < count - 1; i++) {
        const b = clips[i].boundaryOut ?? "cut";
        crossfade[i] = b === "crossfade";
        cont[i] = b === "continue";
        if (crossfade[i] || cont[i]) {
            requested++;
        }
    }
    if (requested === 0) {
        return { overlaps: noOverlap(), fallback: false };
    }
    const frames = clips.map((c) => framesForClip(c.duration, fps));
    let overlap = DEFAULT_CROSSFADE_OVERLAP_FRAMES;
    for (let i = 0; i < count; i++) {
        const fixedTrim =
            (i > 0 && cont[i - 1] ? 1 : 0) + (i < count - 1 && cont[i] ? 1 : 0);
        const crossSides =
            (i > 0 && crossfade[i - 1] ? 1 : 0) +
            (i < count - 1 && crossfade[i] ? 1 : 0);
        if (fixedTrim === 0 && crossSides === 0) {
            continue;
        }
        const budget = frames[i] - 1 - fixedTrim;
        if (
            budget < 0 ||
            (crossSides > 0 && Math.floor(budget / crossSides) < 1)
        ) {
            return { overlaps: noOverlap(), fallback: true };
        }
        if (crossSides > 0) {
            overlap = Math.min(overlap, Math.floor(budget / crossSides));
        }
    }
    const overlaps: number[] = [];
    for (let i = 0; i < count - 1; i++) {
        overlaps[i] = crossfade[i] ? overlap : cont[i] ? 1 : 0;
    }
    return { overlaps, fallback: false };
};

export interface TimelineBoundaryTrack {
    attach(body: HTMLElement): void;
    dispose(): void;
}

const parseLeftClipIdx = (el: Element | null): number | null => {
    if (!el) {
        return null;
    }
    const raw = el.getAttribute("data-left-clip-idx");
    if (raw === null) {
        return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
};

/**
 * Interior seam chips: clicking (or Enter/Space) cycles clip N's outgoing boundary to the next mode
 * AND selects that boundary so the bottom detail strip renders its editor. The strip mirrors the new
 * mode immediately (it reads the clip live after saveClips re-renders the timeline).
 */
export const createTimelineBoundaryTrack = (): TimelineBoundaryTrack => {
    let boundBody: HTMLElement | null = null;
    let unbind: (() => void) | null = null;

    const activateFromTarget = (target: Element): void => {
        const chip = target.closest(CHIP_SELECTOR);
        if (!(chip instanceof HTMLElement)) {
            return;
        }
        const leftClipIdx = parseLeftClipIdx(chip);
        if (leftClipIdx === null) {
            return;
        }
        setSelection({ kind: "boundary", leftClipIdx });
        const clips = getClips();
        const clip = clips[leftClipIdx];
        // Only interior clips (not the final one) own an outgoing boundary.
        if (!clip || leftClipIdx >= clips.length - 1) {
            return;
        }
        clip.boundaryOut = nextBoundary(clip.boundaryOut ?? "cut");
        saveClips(clips, { origin: "boundary-track" });
    };

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        unbind = bindClickSelectableTrack(body, CHIP_SELECTOR, (el) =>
            activateFromTarget(el),
        );
    };

    const dispose = (): void => {
        unbind?.();
        unbind = null;
        boundBody = null;
    };

    return { attach, dispose };
};
