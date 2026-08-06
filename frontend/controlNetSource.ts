export const CONTROLNET_SOURCE_OPTIONS = [
    "ControlNet 1",
    "ControlNet 2",
    "ControlNet 3",
] as const;

export const canonicalControlNetSource = (value: unknown): string | null => {
    const compact = `${value ?? ""}`.trim().replace(/\s+/g, "").toLowerCase();
    if (!compact.startsWith("controlnet")) {
        return null;
    }
    const rawIndex = compact.slice("controlnet".length);
    if (!/^[+-]?\d+$/.test(rawIndex)) {
        return null;
    }
    const oneBased = Number(rawIndex);
    return Number.isSafeInteger(oneBased) && oneBased >= 1 && oneBased <= 3
        ? CONTROLNET_SOURCE_OPTIONS[oneBased - 1]
        : null;
};
