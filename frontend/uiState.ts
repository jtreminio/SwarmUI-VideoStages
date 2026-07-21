import type { Clip } from "./types";
import { isRecord, safeJsonParse } from "./utils";

const UI_STATE_KEY = "videostages_ui_state";

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
    const parsed = safeJsonParse<unknown>(raw, null);
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
