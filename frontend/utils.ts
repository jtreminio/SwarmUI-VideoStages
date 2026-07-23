export const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

/** Parse a finite number without coupling pure domain code to host access. */
export const toNumber = (
    value: string | null | undefined,
    fallback: number,
): number => {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
};

/** Round to the timeline's 0.1-second grid. */
export const roundToTenth = (seconds: number): number =>
    Math.round(seconds * 10) / 10;

/** Round up to the timeline's 0.1-second grid. */
export const gridCeil = (seconds: number): number =>
    Math.ceil(seconds * 10) / 10;

/** Round down to the timeline's 0.1-second grid. */
export const gridFloor = (seconds: number): number =>
    Math.floor(seconds * 10) / 10;

/** Parse JSON, returning `fallback` on nullish input or any parse error. */
export const safeJsonParse = <T>(
    raw: string | null | undefined,
    fallback: T,
): T => {
    if (raw == null) {
        return fallback;
    }
    try {
        return JSON.parse(raw) as T;
    } catch {
        return fallback;
    }
};
