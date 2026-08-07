import {
    CONTROLNET_SOURCE_OPTIONS,
    MEDIA_SOURCE_ACE_STEP_FUN_PREFIX,
    MEDIA_SOURCE_BASE_2_EDIT_PREFIX,
    MEDIA_SOURCE_CONTROLNET,
} from "./generatedMediaSource";

/**
 * Mirrors StringUtils.Compact: trim, then drop the spaces the backend drops.
 * Only literal spaces — matching Compact's `Replace(" ", "")` rather than a
 * `\s` class, so the two sides agree on a tab-bearing value too.
 */
const compact = (value: unknown): string =>
    `${value ?? ""}`.trim().replaceAll(" ", "");

/**
 * Mirrors MediaSource.TryParseNonNegativeIndex. The leading "+" is accepted
 * because the backend parses with int.TryParse, which takes a sign; negatives
 * are rejected there by the `parsed >= 0` guard rather than by the grammar.
 */
export const parseIndexedMediaSource = (
    value: unknown,
    prefix: string,
): number | null => {
    const text = compact(value);
    if (!text.toLowerCase().startsWith(prefix.toLowerCase())) {
        return null;
    }
    const rest = text.slice(prefix.length);
    if (!/^\+?\d+$/.test(rest)) {
        return null;
    }
    const index = Number(rest);
    return Number.isSafeInteger(index) ? index : null;
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
