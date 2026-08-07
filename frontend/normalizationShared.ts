import { clamp } from "./constants";
import { toNumber } from "./utils";

/** Text from any stored value; absent reads as `fallback` ("" by default). */
export const text = (value: unknown, fallback: unknown = ""): string =>
    `${value ?? fallback}`;

/** `text`, trimmed. */
export const trimmedText = (value: unknown, fallback: unknown = ""): string =>
    text(value, fallback).trim();

/** Text that falls back when the stored value is absent OR empty. */
export const textOr = (value: unknown, fallback: string): string =>
    text(value, fallback) || fallback;

/** Finite number from any stored value, else `fallback`. */
export const numberOr = (value: unknown, fallback: number): number =>
    toNumber(text(value, fallback), fallback);

/** `numberOr` clamped into [min, max] — the common stored-slider shape. */
export const clampedNumber = (
    value: unknown,
    fallback: number,
    min: number,
    max: number,
): number => clamp(numberOr(value, fallback), min, max);

/** Non-negative finite number; absent, blank, or negative reads as 0. */
export const nonNegativeNumber = (value: unknown): number =>
    Math.max(0, numberOr(value, 0));

/** A stored optional number: null unless it is present and non-negative. */
export const optionalNonNegativeNumber = (value: unknown): number | null => {
    if (value == null || trimmedText(value) === "") {
        return null;
    }
    const number = toNumber(text(value), Number.NaN);
    return Number.isFinite(number) && number >= 0 ? number : null;
};

/** As `optionalNonNegativeNumber`, but zero is not a value either. */
export const optionalPositiveNumber = (value: unknown): number | null => {
    const number = optionalNonNegativeNumber(value);
    return number !== null && number > 0 ? number : null;
};

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
): string | undefined =>
    typeof value === "string" ? value.trim() || undefined : undefined;

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

export const snapValueToStep = (
    value: unknown,
    fallback: number,
    min: number,
    max: number,
    step: number,
): number => {
    const unitScale = 1 / step;
    return (
        Math.round(clampedNumber(value, fallback, min, max) * unitScale) /
        unitScale
    );
};
