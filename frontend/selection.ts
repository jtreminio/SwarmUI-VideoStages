import type { TimelineSelection } from "./selectionTypes";

const NO_SELECTION: TimelineSelection = { kind: "none" };
let selection: TimelineSelection = NO_SELECTION;
const selectionSubscribers = new Set<(sel: TimelineSelection) => void>();

const clipIdxOf = (sel: TimelineSelection): number | null =>
    sel.kind === "none" || sel.kind === "boundary" ? null : sel.clipIdx;

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
    if (a.clipIdx !== clipIdxOf(b)) {
        return false;
    }
    if (a.kind === "clip" && b.kind === "clip") {
        return a.stageIdx === b.stageIdx;
    }
    if (a.kind === "ref" && b.kind === "ref") {
        return a.refIdx === b.refIdx;
    }
    if (a.kind === "prompt-minor" && b.kind === "prompt-minor") {
        return a.windowIdx === b.windowIdx;
    }
    if (a.kind === "audio-segment" && b.kind === "audio-segment") {
        return a.segIdx === b.segIdx;
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
