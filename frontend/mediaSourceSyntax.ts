import {
    CONTROLNET_SOURCE_OPTIONS,
    MEDIA_SOURCE_ACE_STEP_FUN_PREFIX,
    MEDIA_SOURCE_BASE_2_EDIT_PREFIX,
    MEDIA_SOURCE_CONTROLNET,
} from "./generatedMediaSource";

/**
 * Mirrors StringUtils.Compact: trim, then drop the spaces the backend drops.
 * Only literal spaces, matching Compact's `Replace(" ", "")` — a `\s` class
 * would accept tabs the backend leaves in place.
 */
export const compactMediaSource = (value: unknown): string =>
    `${value ?? ""}`.trim().replaceAll(" ", "");

/** Mirrors StringUtils.Equals, which every backend source comparison goes through. */
export const equalsMediaSource = (left: string, right: string): boolean =>
    left.toLowerCase() === right.toLowerCase();

/** Beyond this the backend's int.TryParse overflows and rejects the value. */
const INT_MAX = 2147483647;

/** Mirrors MediaSource.TryParseNonNegativeIndex. */
export const parseIndexedMediaSource = (
    value: unknown,
    prefix: string,
): number | null => {
    const text = compactMediaSource(value);
    if (!text.toLowerCase().startsWith(prefix.toLowerCase())) {
        return null;
    }
    const rest = text.slice(prefix.length);
    if (!/^\d+$/.test(rest)) {
        return null;
    }
    const index = Number(rest);
    return index <= INT_MAX ? index : null;
};

export const parseAceStepFunIndex = (value: unknown): number | null =>
    parseIndexedMediaSource(value, MEDIA_SOURCE_ACE_STEP_FUN_PREFIX);

export const parseBase2EditStageIndex = (value: unknown): number | null =>
    parseIndexedMediaSource(value, MEDIA_SOURCE_BASE_2_EDIT_PREFIX);

/** The canonical slot spelling, or null when the value names no real slot. */
export const canonicalControlNetSource = (value: unknown): string | null => {
    const oneBased = parseIndexedMediaSource(value, MEDIA_SOURCE_CONTROLNET);
    return oneBased !== null &&
        oneBased >= 1 &&
        oneBased <= CONTROLNET_SOURCE_OPTIONS.length
        ? CONTROLNET_SOURCE_OPTIONS[oneBased - 1]
        : null;
};
