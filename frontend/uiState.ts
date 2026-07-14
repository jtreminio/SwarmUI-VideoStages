import type { Clip, TimelineSelection } from "./types";

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

interface StoredRefUi {
    expanded: boolean;
}

interface StoredStageUi {
    expanded: boolean;
}

interface StoredClipUi {
    hue: number | null;
    expanded: boolean;
    stages: StoredStageUi[];
    refs: StoredRefUi[];
}

interface StoredUiState {
    clips: StoredClipUi[];
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

export const serializeUiState = (clips: Clip[]): string => {
    const state: StoredUiState = {
        clips: clips.map((clip) => ({
            hue: typeof clip.hue === "number" ? clip.hue : null,
            expanded: clip.expanded !== false,
            stages: clip.stages.map((stage) => ({
                expanded: stage.expanded !== false,
            })),
            refs: clip.refs.map((ref) => ({
                expanded: ref.expanded !== false,
            })),
        })),
    };
    return JSON.stringify(state);
};

export const applyUiStateFrom = (raw: string | null, clips: Clip[]): void => {
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
        if (typeof stored.expanded === "boolean") {
            clips[i].expanded = stored.expanded;
        }
        const stages = Array.isArray(stored.stages) ? stored.stages : [];
        for (let s = 0; s < clips[i].stages.length; s++) {
            const storedStage = stages[s];
            if (
                isRecord(storedStage) &&
                typeof storedStage.expanded === "boolean"
            ) {
                clips[i].stages[s].expanded = storedStage.expanded;
            }
        }
        const refs = Array.isArray(stored.refs) ? stored.refs : [];
        for (let r = 0; r < clips[i].refs.length; r++) {
            const storedRef = refs[r];
            if (
                isRecord(storedRef) &&
                typeof storedRef.expanded === "boolean"
            ) {
                clips[i].refs[r].expanded = storedRef.expanded;
            }
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
