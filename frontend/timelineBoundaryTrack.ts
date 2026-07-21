import { getClips, saveClips } from "./persistence";
import { setSelection } from "./selection";
import { bindClickSelectableTrack } from "./trackDomUtils";
import type { BoundaryOut } from "./types";

const CHIP_SELECTOR = "[data-vst-boundary-cycle]";

const CYCLE: BoundaryOut[] = ["cut", "continue", "crossfade"];

export const nextBoundary = (current: BoundaryOut): BoundaryOut => {
    const idx = CYCLE.indexOf(current);
    return CYCLE[(idx + 1) % CYCLE.length];
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
