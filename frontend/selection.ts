import type { TimelineSelection } from "./selectionTypes";

const NO_SELECTION: TimelineSelection = { kind: "none" };
let selection: TimelineSelection = NO_SELECTION;
const selectionSubscribers = new Set<(sel: TimelineSelection) => void>();

const clipIdxOf = (sel: TimelineSelection): number | null =>
    sel.kind === "none" ||
    sel.kind === "boundary" ||
    sel.kind === "audio-track" ||
    sel.kind === "audio-track-span"
        ? null
        : sel.clipIdx;

const sameSelection = (a: TimelineSelection, b: TimelineSelection): boolean => {
    if (a.kind !== b.kind) {
        return false;
    }
    if (a.kind === "none" || b.kind === "none") {
        return true;
    }
    if (a.kind === "boundary" || b.kind === "boundary") {
        return (
            a.kind === "boundary" &&
            b.kind === "boundary" &&
            a.leftClipIdx === b.leftClipIdx
        );
    }
    if (a.kind === "audio-track" && b.kind === "audio-track") {
        return a.trackIdx === b.trackIdx;
    }
    if (a.kind === "audio-track-span" && b.kind === "audio-track-span") {
        return a.trackIdx === b.trackIdx && a.spanIdx === b.spanIdx;
    }
    if (
        a.kind === "audio-track" ||
        b.kind === "audio-track" ||
        a.kind === "audio-track-span" ||
        b.kind === "audio-track-span"
    ) {
        return false;
    }
    if (a.clipIdx !== clipIdxOf(b)) {
        return false;
    }
    if (a.kind === "clip" && b.kind === "clip") {
        return a.stageIdx === b.stageIdx;
    }
    if (a.kind === "ref" && b.kind === "ref") {
        return a.refIdx === b.refIdx;
    }
    if (a.kind === "ic-lora" && b.kind === "ic-lora") {
        return a.entryIdx === b.entryIdx;
    }
    if (a.kind === "prompt-minor" && b.kind === "prompt-minor") {
        return a.windowIdx === b.windowIdx;
    }
    return true;
};

export const isSameSelection = (
    a: TimelineSelection,
    b: TimelineSelection,
): boolean => sameSelection(a, b);

export const getSelection = (): TimelineSelection => selection;

export const getSelectedClipIndex = (): number | null => clipIdxOf(selection);

export const setSelection = (next: TimelineSelection): void => {
    if (sameSelection(selection, next)) {
        return;
    }
    selection = next;
    for (const cb of [...selectionSubscribers]) {
        try {
            cb(selection);
        } catch {}
    }
};

/**
 * Select and notify even when the same item is activated again. Timeline marks
 * use this when activation should also reveal the owning sidebar repeater.
 */
export const activateSelection = (next: TimelineSelection): void => {
    selection = next;
    for (const cb of [...selectionSubscribers]) {
        try {
            cb(selection);
        } catch {}
    }
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
    selection = NO_SELECTION;
    selectionSubscribers.clear();
};
