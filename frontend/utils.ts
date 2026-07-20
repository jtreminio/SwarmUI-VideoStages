export const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

/** Round to the timeline's 0.1-second grid. */
export const roundToTenth = (seconds: number): number =>
    Math.round(seconds * 10) / 10;

const getElementByType = <T extends Element>(
    id: string,
    ctor: { new (): T },
): T | null => {
    const element = document.getElementById(id);
    return element instanceof ctor ? element : null;
};

export const utils = {
    getInputElement: (id: string): HTMLInputElement | null =>
        getElementByType(id, HTMLInputElement),

    getSelectElement: (id: string): HTMLSelectElement | null =>
        getElementByType(id, HTMLSelectElement),

    getSelectValues: (select: HTMLSelectElement | null): string[] =>
        select ? Array.from(select.options, (option) => option.value) : [],

    getSelectLabels: (select: HTMLSelectElement | null): string[] =>
        select ? Array.from(select.options, (option) => option.label) : [],

    toNumber: (value: string | null | undefined, fallback: number): number => {
        const parsed = Number(value);
        return Number.isFinite(parsed) ? parsed : fallback;
    },
};
