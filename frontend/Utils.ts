export const VideoStageUtils = {
    getInputElement: (id: string): HTMLInputElement | null =>
        document.getElementById(id) as HTMLInputElement | null,

    getSelectElement: (id: string): HTMLSelectElement | null =>
        document.getElementById(id) as HTMLSelectElement | null,

    getSelectValues: (select: HTMLSelectElement | null): string[] =>
        select ? Array.from(select.options, (option) => option.value) : [],

    getSelectLabels: (select: HTMLSelectElement | null): string[] =>
        select ? Array.from(select.options, (option) => option.label) : [],

    toNumber: (value: string | null | undefined, fallback: number): number => {
        const parsed = Number(value);
        return Number.isFinite(parsed) ? parsed : fallback;
    },
};
