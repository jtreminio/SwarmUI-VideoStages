import { getVideoStagesHostBridge } from "./host";

/** Accessors for SwarmUI's host form elements (by id) and their values. */
export const utils = {
    getInputElement: (id: string): HTMLInputElement | null =>
        getVideoStagesHostBridge().getInput(id),

    getSelectElement: (id: string): HTMLSelectElement | null =>
        getVideoStagesHostBridge().getSelect(id),

    getSelectValues: (select: HTMLSelectElement | null): string[] =>
        getVideoStagesHostBridge().getSelectOptions(select).values,

    getSelectLabels: (select: HTMLSelectElement | null): string[] =>
        getVideoStagesHostBridge().getSelectOptions(select).labels,
};
