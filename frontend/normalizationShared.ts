import { clamp } from "./constants";
import { toNumber } from "./utils";

export const readProp = (
    raw: Record<string, unknown>,
    ...keys: string[]
): unknown => {
    for (const key of keys) {
        if (Object.hasOwn(raw, key)) {
            return raw[key];
        }
    }
    return undefined;
};

export const normalizeOptionalEntityId = (
    value: unknown,
): string | undefined => {
    if (typeof value !== "string") {
        return undefined;
    }
    const id = value.trim();
    return id || undefined;
};

export const snapToStep = (value: number, step: number): number =>
    step > 0 ? Math.round(value / step) * step : value;

/**
 * Clamp a (start, length) window inside [0, clipDuration] with a minimum
 * length: start is clamped so at least minLength fits, then length is clamped
 * to what remains. Returns null when no positive-length window survives.
 */
export const clampWindowInDuration = (
    startRaw: number,
    lengthRaw: number,
    clipDuration: number,
    minLength: number,
): { startSeconds: number; lengthSeconds: number } | null => {
    if (!(lengthRaw > 0)) {
        return null;
    }
    const maxStart = Math.max(0, clipDuration - minLength);
    const startSeconds = clamp(startRaw, 0, maxStart);
    const lengthSeconds = clamp(
        lengthRaw,
        minLength,
        Math.max(minLength, clipDuration - startSeconds),
    );
    if (!(lengthSeconds > 0)) {
        return null;
    }
    return { startSeconds, lengthSeconds };
};

export const resolveRootPreferredUpscaleMethod = (
    upscaleMethodValues: string[],
): string =>
    upscaleMethodValues.find((value) =>
        value.trim().toLowerCase().startsWith("latentmodel-"),
    ) ??
    upscaleMethodValues[0] ??
    "";

export const snapValueToStep = (
    value: unknown,
    fallback: number,
    min: number,
    max: number,
    step: number,
): number => {
    const unitScale = 1 / step;
    return (
        Math.round(
            clamp(toNumber(`${value ?? fallback}`, fallback), min, max) *
                unitScale,
        ) / unitScale
    );
};
