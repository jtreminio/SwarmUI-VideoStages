import type { Clip } from "./types";

const UI_STATE_KEY = "videostages_ui_state";

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
