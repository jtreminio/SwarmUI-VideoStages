import type { Clip, TimelineSelection } from "./types";
import { isRecord } from "./utils";

const UI_STATE_KEY = "videostages_ui_state";

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

/** Structural equality for two selections. */
export const isSameSelection = (
    a: TimelineSelection,
    b: TimelineSelection,
): boolean => sameSelection(a, b);

/** The single source of truth for what the detail strip is editing. */
export const getSelection = (): TimelineSelection => selection;

/** Selected clip index for any clip-bound selection kind, else null. */
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

interface StoredClipUi {
    hue: number | null;
}

interface StoredUiState {
    clips: StoredClipUi[];
}

const serializeUiState = (clips: Clip[]): string => {
    const state: StoredUiState = {
        clips: clips.map((clip) => ({
            hue: typeof clip.hue === "number" ? clip.hue : null,
        })),
    };
    return JSON.stringify(state);
};

const applyUiStateFrom = (raw: string | null, clips: Clip[]): void => {
    if (!raw) {
        return;
    }
    let parsed: unknown;
    try {
        parsed = JSON.parse(raw);
    } catch {
        return;
    }
    const storedClips =
        isRecord(parsed) && Array.isArray(parsed.clips) ? parsed.clips : [];
    for (let i = 0; i < clips.length; i++) {
        const stored = storedClips[i];
        if (!isRecord(stored)) {
            continue;
        }
        if (typeof stored.hue === "number" && Number.isFinite(stored.hue)) {
            clips[i].hue = stored.hue;
        }
    }
};

export const applyUiState = (clips: Clip[]): void => {
    try {
        applyUiStateFrom(localStorage.getItem(UI_STATE_KEY), clips);
    } catch {}
};

export const saveUiState = (clips: Clip[]): void => {
    try {
        localStorage.setItem(UI_STATE_KEY, serializeUiState(clips));
    } catch {}
};

export const clearUiStateForTests = (): void => {
    try {
        localStorage.removeItem(UI_STATE_KEY);
    } catch {}
};
