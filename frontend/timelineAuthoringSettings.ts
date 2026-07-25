const SETTINGS_KEY = "videostages.timeline.authoringSettings";

export interface TimelineAuthoringSettings {
    snap: boolean;
    autoCollapse: boolean;
}

export type TimelineAuthoringSetting = keyof TimelineAuthoringSettings;

const DEFAULT_SETTINGS: TimelineAuthoringSettings = {
    snap: true,
    autoCollapse: true,
};

export const getTimelineAuthoringSettings = (): TimelineAuthoringSettings => {
    try {
        const raw = localStorage.getItem(SETTINGS_KEY);
        if (!raw) {
            return { ...DEFAULT_SETTINGS };
        }
        const parsed = JSON.parse(raw) as {
            snap?: unknown;
            autoCollapse?: unknown;
        };
        return {
            snap:
                typeof parsed.snap === "boolean"
                    ? parsed.snap
                    : DEFAULT_SETTINGS.snap,
            autoCollapse:
                typeof parsed.autoCollapse === "boolean"
                    ? parsed.autoCollapse
                    : DEFAULT_SETTINGS.autoCollapse,
        };
    } catch {
        return { ...DEFAULT_SETTINGS };
    }
};

export const setTimelineAuthoringSetting = (
    key: TimelineAuthoringSetting,
    value: boolean,
): void => {
    const next = {
        ...getTimelineAuthoringSettings(),
        [key]: value,
    };
    try {
        localStorage.setItem(SETTINGS_KEY, JSON.stringify(next));
    } catch {}
};
