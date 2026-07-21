import type { TimelineUnit } from "./timelineDetail";

const VIEW_STATE_KEY = "videostages.timeline.viewState";

export interface StoredViewState {
    pxPerSecond?: number;
    unit?: TimelineUnit;
    stripCollapsed?: boolean;
}

/** Reads the persisted view state (zoom / unit / strip collapse), else null. */
export const loadViewState = (): StoredViewState | null => {
    try {
        const raw = localStorage.getItem(VIEW_STATE_KEY);
        if (!raw) {
            return null;
        }
        const parsed = JSON.parse(raw) as {
            pxPerSecond?: unknown;
            unit?: unknown;
            stripCollapsed?: unknown;
        };
        const state: StoredViewState = {};
        if (typeof parsed.pxPerSecond === "number") {
            state.pxPerSecond = parsed.pxPerSecond;
        }
        if (parsed.unit === "frames" || parsed.unit === "seconds") {
            state.unit = parsed.unit;
        }
        if (typeof parsed.stripCollapsed === "boolean") {
            state.stripCollapsed = parsed.stripCollapsed;
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
