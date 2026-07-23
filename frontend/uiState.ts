import { ensureClipEntityIdentities } from "./identity";
import type { Clip } from "./types";
import { isRecord, safeJsonParse } from "./utils";

const UI_STATE_KEY = "videostages_ui_state";
const UI_STATE_SCHEMA_VERSION = 2;

interface StoredPromptWindowUi {
    id: string;
    prompt: string;
    start: number;
    duration: number;
}

interface StoredClipUi {
    id?: string;
    hue: number | null;
    promptWindows?: StoredPromptWindowUi[];
}

interface StoredUiState {
    schemaVersion?: number;
    clips: StoredClipUi[];
}

const serializeUiState = (clips: Clip[]): string => {
    ensureClipEntityIdentities(clips);
    const state: StoredUiState = {
        schemaVersion: UI_STATE_SCHEMA_VERSION,
        clips: clips.map((clip) => ({
            id: clip.id,
            hue: typeof clip.hue === "number" ? clip.hue : null,
            promptWindows: clip.promptWindows.map((window) => ({
                id: window.id as string,
                prompt: window.prompt,
                start: window.start,
                duration: window.duration,
            })),
        })),
    };
    return JSON.stringify(state);
};

const promptWindowKey = (
    window: Pick<StoredPromptWindowUi, "prompt" | "start" | "duration">,
): string => JSON.stringify([window.prompt, window.start, window.duration]);

const restorePromptWindowIds = (
    stored: Record<string, unknown>,
    clip: Clip,
): void => {
    const rawWindows = Array.isArray(stored.promptWindows)
        ? stored.promptWindows
        : [];
    const idsByWindow = new Map<string, string[]>();
    for (const rawWindow of rawWindows) {
        if (
            !isRecord(rawWindow) ||
            typeof rawWindow.id !== "string" ||
            !rawWindow.id.trim() ||
            typeof rawWindow.prompt !== "string" ||
            typeof rawWindow.start !== "number" ||
            typeof rawWindow.duration !== "number"
        ) {
            continue;
        }
        const key = promptWindowKey({
            prompt: rawWindow.prompt,
            start: rawWindow.start,
            duration: rawWindow.duration,
        });
        const ids = idsByWindow.get(key) ?? [];
        ids.push(rawWindow.id.trim());
        idsByWindow.set(key, ids);
    }
    for (const window of clip.promptWindows) {
        const ids = idsByWindow.get(promptWindowKey(window));
        const id = ids?.shift();
        if (id) {
            window.id = id;
        }
    }
};

const applyUiStateFrom = (raw: string | null, clips: Clip[]): void => {
    if (!raw) {
        return;
    }
    const parsed = safeJsonParse<unknown>(raw, null);
    const storedClips =
        isRecord(parsed) && Array.isArray(parsed.clips) ? parsed.clips : [];
    const storedById = new Map<string, Record<string, unknown>>();
    for (const stored of storedClips) {
        if (
            isRecord(stored) &&
            typeof stored.id === "string" &&
            stored.id.trim()
        ) {
            storedById.set(stored.id.trim(), stored);
        }
    }
    for (let i = 0; i < clips.length; i++) {
        const clipId = clips[i].id;
        const positional = storedClips[i];
        const stored =
            (clipId ? storedById.get(clipId) : undefined) ??
            (isRecord(positional) && !positional.id ? positional : undefined);
        if (!isRecord(stored)) {
            continue;
        }
        if (typeof stored.hue === "number" && Number.isFinite(stored.hue)) {
            clips[i].hue = stored.hue;
        }
        restorePromptWindowIds(stored, clips[i]);
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
