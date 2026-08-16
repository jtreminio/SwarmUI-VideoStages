import { normalizeLoraFolders } from "./loraFolderFilter";

const SETTINGS_KEY = "videostages.timeline.authoringSettings";

export const TIMELINE_AUTHORING_SETTINGS_CHANGED =
    "videostages:timeline-authoring-settings-changed";

export type DimensionSnapSetting = "disabled" | 32 | 64;

export interface TimelineAuthoringSettings {
    snap: boolean;
    autoCollapse: boolean;
    dimensionSnap: DimensionSnapSetting;
    loraFolders: string[] | null;
}

export type TimelineAuthoringSetting = keyof TimelineAuthoringSettings;

const DEFAULT_SETTINGS: TimelineAuthoringSettings = {
    snap: true,
    autoCollapse: true,
    dimensionSnap: "disabled",
    loraFolders: null,
};

const dimensionSnapSetting = (value: unknown): DimensionSnapSetting =>
    value === 32 || value === 64 ? value : "disabled";

export const getTimelineAuthoringSettings = (): TimelineAuthoringSettings => {
    try {
        const raw = localStorage.getItem(SETTINGS_KEY);
        if (!raw) {
            return { ...DEFAULT_SETTINGS };
        }
        const parsed = JSON.parse(raw) as {
            snap?: unknown;
            autoCollapse?: unknown;
            dimensionSnap?: unknown;
            loraFolders?: unknown;
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
            dimensionSnap: dimensionSnapSetting(parsed.dimensionSnap),
            loraFolders: normalizeLoraFolders(parsed.loraFolders),
        };
    } catch {
        return { ...DEFAULT_SETTINGS };
    }
};

export const setTimelineAuthoringSetting = <K extends TimelineAuthoringSetting>(
    key: K,
    value: TimelineAuthoringSettings[K],
): void => {
    const next = {
        ...getTimelineAuthoringSettings(),
        [key]: value,
    };
    try {
        localStorage.setItem(SETTINGS_KEY, JSON.stringify(next));
    } catch {}
};
