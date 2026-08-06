import {
    anchorSelection,
    isSameSelection,
    resolveSelection,
    type SelectionAnchor,
    sameAnchor,
    selectionAfterRemoval,
} from "./selectionIdentity";
import type { TimelineSelection } from "./selectionTypes";

export { isSameSelection, selectionAfterRemoval };

const NO_SELECTION: SelectionAnchor = { selection: { kind: "none" } };
let anchor: SelectionAnchor = NO_SELECTION;
const selectionSubscribers = new Set<(sel: TimelineSelection) => void>();

const clipIdxOf = (sel: TimelineSelection): number | null =>
    sel.kind === "none" ||
    sel.kind === "boundary" ||
    sel.kind === "boundary-ref" ||
    sel.kind === "audio-track"
        ? null
        : sel.clipIdx;

/**
 * The live selection: stable entity ids resolved against the current document,
 * so callers keep reading plain array positions while structural edits and
 * history restores can no longer retarget the dock.
 */
export const getSelection = (): TimelineSelection => resolveSelection(anchor);

export const getSelectedClipIndex = (): number | null =>
    clipIdxOf(getSelection());

const notify = (): void => {
    const current = getSelection();
    for (const cb of [...selectionSubscribers]) {
        try {
            cb(current);
        } catch {}
    }
};

export const setSelection = (next: TimelineSelection): void => {
    const nextAnchor = anchorSelection(next);
    if (sameAnchor(anchor, nextAnchor)) {
        return;
    }
    anchor = nextAnchor;
    notify();
};

/**
 * Select and notify even when the same item is activated again. Timeline marks
 * use this when activation should also reveal the owning sidebar repeater.
 */
export const activateSelection = (next: TimelineSelection): void => {
    anchor = anchorSelection(next);
    notify();
};

export const subscribeSelection = (
    cb: (sel: TimelineSelection) => void,
): (() => void) => {
    selectionSubscribers.add(cb);
    return () => {
        selectionSubscribers.delete(cb);
    };
};

export const resetSelectionForTests = (): void => {
    anchor = NO_SELECTION;
    selectionSubscribers.clear();
};
