import { getLtxHostBridge } from "./host";

/** Accessors for SwarmUI's host form elements (by id) and their values. */
export const utils = {
    getInputElement: (id: string): HTMLInputElement | null =>
        getLtxHostBridge().getInput(id),

    getSelectElement: (id: string): HTMLSelectElement | null =>
        getLtxHostBridge().getSelect(id),

    getSelectValues: (select: HTMLSelectElement | null): string[] =>
        getLtxHostBridge().getSelectOptions(select).values,

    getSelectLabels: (select: HTMLSelectElement | null): string[] =>
        getLtxHostBridge().getSelectOptions(select).labels,
};
