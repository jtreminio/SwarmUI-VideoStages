export interface DropRegion {
    startPx: number;
    widthPx: number;
}

export const computeDropIndex = (
    pointerX: number,
    regions: DropRegion[],
): number => {
    for (let i = 0; i < regions.length; i++) {
        const region = regions[i];
        const midpoint = region.startPx + region.widthPx / 2;
        if (pointerX < midpoint) {
            return i;
        }
    }
    return regions.length;
};

export const finalIndexAfterMove = (from: number, to: number): number =>
    to > from ? to - 1 : to;

export const moveItem = <T>(array: T[], from: number, to: number): T[] => {
    const result = array.slice();
    if (!Number.isInteger(from) || from < 0 || from >= result.length) {
        return result;
    }
    const [item] = result.splice(from, 1);
    const insertAt = to > from ? to - 1 : to;
    const clamped = Math.max(0, Math.min(insertAt, result.length));
    result.splice(clamped, 0, item);
    return result;
};

export const isNoOpMove = (from: number, to: number): boolean =>
    to === from || to === from + 1;
