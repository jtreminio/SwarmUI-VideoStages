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
