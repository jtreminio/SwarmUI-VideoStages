import type { TimelineUnit } from "./timelineDetail";

const VIEW_STATE_KEY = "videostages.timeline.viewState";

export interface StoredViewState {
    pxPerSecond?: number;
    unit?: TimelineUnit;
}

/** Reads the persisted timeline zoom/unit state, else null. */
export const loadViewState = (): StoredViewState | null => {
    try {
        const raw = localStorage.getItem(VIEW_STATE_KEY);
        if (!raw) {
            return null;
        }
        const parsed = JSON.parse(raw) as {
            pxPerSecond?: unknown;
            unit?: unknown;
        };
        const state: StoredViewState = {};
        if (typeof parsed.pxPerSecond === "number") {
            state.pxPerSecond = parsed.pxPerSecond;
        }
        if (parsed.unit === "frames" || parsed.unit === "seconds") {
            state.unit = parsed.unit;
        }
        return state;
    } catch {
        return null;
    }
};

export const saveViewState = (state: StoredViewState): void => {
    try {
        localStorage.setItem(VIEW_STATE_KEY, JSON.stringify(state));
    } catch {}
};
