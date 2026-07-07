export const HUE_MIN = 0;
export const HUE_MAX = 359;
const HUE_RANGE = 360;
export const BASE_HUE = 210;
export const UNASSIGNED_HUE = -1;
const HUE_SATURATION = 65;
const HUE_LIGHTNESS = 55;

export const isAssignedHue = (value: unknown): value is number =>
    typeof value === "number" &&
    Number.isInteger(value) &&
    value >= HUE_MIN &&
    value <= HUE_MAX;

export const normalizeStoredHue = (value: unknown): number => {
    if (value == null || value === "") {
        return UNASSIGNED_HUE;
    }
    const num = typeof value === "number" ? value : Number(value);
    if (!Number.isFinite(num)) {
        return UNASSIGNED_HUE;
    }
    return ((Math.round(num) % HUE_RANGE) + HUE_RANGE) % HUE_RANGE;
};

const hueDistance = (a: number, b: number): number => {
    const d = Math.abs(a - b) % HUE_RANGE;
    return d > 180 ? HUE_RANGE - d : d;
};

export const pickDistinctHue = (existing: readonly number[]): number => {
    const inUse = existing.filter(isAssignedHue);
    if (inUse.length === 0) {
        return BASE_HUE;
    }
    let best = 0;
    let bestScore = -1;
    for (let hue = HUE_MIN; hue <= HUE_MAX; hue++) {
        let minDist = 180;
        for (const used of inUse) {
            const d = hueDistance(hue, used);
            if (d < minDist) {
                minDist = d;
            }
        }
        if (minDist > bestScore) {
            bestScore = minDist;
            best = hue;
        }
    }
    return best;
};

export const assignMissingHues = (clips: { hue: number }[]): void => {
    const assigned: number[] = [];
    for (const clip of clips) {
        if (isAssignedHue(clip.hue)) {
            assigned.push(clip.hue);
        }
    }
    for (const clip of clips) {
        if (isAssignedHue(clip.hue)) {
            continue;
        }
        const hue = pickDistinctHue(assigned);
        clip.hue = hue;
        assigned.push(hue);
    }
};

export const clipHueCss = (hue: number): string => {
    const resolved = isAssignedHue(hue) ? hue : BASE_HUE;
    return `hsl(${resolved} ${HUE_SATURATION}% ${HUE_LIGHTNESS}%)`;
};
