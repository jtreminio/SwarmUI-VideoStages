export const VideoStageUtils = {
    getInputElement: (id: string): HTMLInputElement | null => {
        return document.getElementById(id) as HTMLInputElement | null;
    },
    getSelectElement: (id: string): HTMLSelectElement | null => {
        return document.getElementById(id) as HTMLSelectElement | null;
    },
    getSelectValues: (select: HTMLSelectElement | null): string[] => {
        if (!select) {
            return [];
        }
        return Array.from(select.options).map((option) => option.value);
    },
    getSelectLabels: (select: HTMLSelectElement | null): string[] => {
        if (!select) {
            return [];
        }
        return Array.from(select.options).map((option) => option.label);
    },
    toNumber: (value: string | null | undefined, fallback: number): number => {
        const parsed = Number(value);
        return Number.isFinite(parsed) ? parsed : fallback;
    },
};
