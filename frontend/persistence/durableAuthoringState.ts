import { videoStagesDebugLog } from "../debugLog";
import type { ClipTextInput } from "../promptSegments";
import type { VideoStagesConfig } from "../types";
import { isRecord } from "../utils";
import { serializeStateForDurableStorage } from "./documentCodec";

const DURABLE_AUTHORING_KEY = "videostages_authoring_state_v1";
const DURABLE_AUTHORING_VERSION = 1;

export interface DurableAuthoringSnapshot {
    document: string;
    prompts: ClipTextInput[];
}

const isFiniteNumber = (value: unknown): value is number =>
    typeof value === "number" && Number.isFinite(value);

const readPrompt = (value: unknown): ClipTextInput | null => {
    if (!isRecord(value) || typeof value.prompt !== "string") {
        return null;
    }
    if (!Array.isArray(value.windows)) {
        return null;
    }
    const windows: ClipTextInput["windows"] = [];
    for (const window of value.windows) {
        if (
            !isRecord(window) ||
            typeof window.prompt !== "string" ||
            !isFiniteNumber(window.start) ||
            !isFiniteNumber(window.duration)
        ) {
            return null;
        }
        windows.push({
            prompt: window.prompt,
            start: window.start,
            duration: window.duration,
        });
    }
    return { prompt: value.prompt, windows };
};

export const loadDurableAuthoringState =
    (): DurableAuthoringSnapshot | null => {
        try {
            const raw = localStorage.getItem(DURABLE_AUTHORING_KEY);
            if (!raw) {
                return null;
            }
            const parsed: unknown = JSON.parse(raw);
            if (
                !isRecord(parsed) ||
                parsed.version !== DURABLE_AUTHORING_VERSION ||
                typeof parsed.document !== "string" ||
                !Array.isArray(parsed.prompts)
            ) {
                return null;
            }
            const prompts: ClipTextInput[] = [];
            for (const prompt of parsed.prompts) {
                const normalized = readPrompt(prompt);
                if (!normalized) {
                    return null;
                }
                prompts.push(normalized);
            }
            return { document: parsed.document, prompts };
        } catch (error) {
            videoStagesDebugLog(
                "persistence",
                "durable authoring state read failed",
                { error },
            );
            return null;
        }
    };

export const saveDurableAuthoringState = (
    state: VideoStagesConfig,
): DurableAuthoringSnapshot | null => {
    try {
        const snapshot: DurableAuthoringSnapshot = {
            document: serializeStateForDurableStorage(state),
            prompts: state.clips.map((clip) => ({
                prompt: clip.prompt,
                windows: clip.promptWindows.map((window) => ({
                    prompt: window.prompt,
                    start: window.start,
                    duration: window.duration,
                })),
            })),
        };
        const stored = {
            version: DURABLE_AUTHORING_VERSION,
            ...snapshot,
        };
        localStorage.setItem(DURABLE_AUTHORING_KEY, JSON.stringify(stored));
        return snapshot;
    } catch (error) {
        console.warn(
            "VideoStages: durable authoring state could not be saved.",
            error,
        );
        videoStagesDebugLog(
            "persistence",
            "durable authoring state write failed",
            { error },
        );
        return null;
    }
};

export const clearDurableAuthoringState = (): void => {
    try {
        localStorage.removeItem(DURABLE_AUTHORING_KEY);
    } catch {}
};
