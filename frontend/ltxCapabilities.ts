/**
 * Frontend model-family gate for the extension's only supported execution
 * family. Prefer SwarmUI's model metadata; filename matching is a fallback for
 * early page-load/test contexts where the model registry is not available yet.
 */

import { getLtxHostBridge } from "./host";

const LTX_V2_COMPAT_ID = "ltxv2";
const LTX_NAME_PATTERN = /(^|[/\\_. -])ltx(?:v?2(?:[_. -]\d+)?)?($|[/\\_. -])/i;

const compactCompatId = (value: unknown): string =>
    `${value ?? ""}`
        .trim()
        .replace(/[^a-z0-9]/gi, "")
        .toLowerCase();

export const isLtxV2CompatId = (value: unknown): boolean =>
    compactCompatId(value) === LTX_V2_COMPAT_ID;

export const modelCompatId = (modelName: string): string | null => {
    return getLtxHostBridge().getModelCompatId(modelName);
};

export const isLtxVideoModelValue = (modelName: string): boolean => {
    const value = `${modelName ?? ""}`.trim();
    if (!value) {
        return false;
    }
    const compatId = modelCompatId(value);
    if (compatId !== null) {
        return isLtxV2CompatId(compatId);
    }
    return LTX_NAME_PATTERN.test(value);
};

export interface ModelOptionList {
    values: string[];
    labels: string[];
}

export const filterLtxModelOptions = (
    values: readonly string[],
    labels: readonly string[],
): ModelOptionList => {
    const filtered: ModelOptionList = { values: [], labels: [] };
    values.forEach((value, index) => {
        if (!isLtxVideoModelValue(value)) {
            return;
        }
        filtered.values.push(value);
        filtered.labels.push(labels[index] ?? value);
    });
    return filtered;
};

export const isCurrentRootLtxVideoModel = (modelName: string): boolean => {
    const value = `${modelName ?? ""}`.trim();
    if (!value) {
        return false;
    }
    const compatId = modelCompatId(value);
    if (compatId !== null) {
        return isLtxV2CompatId(compatId);
    }
    if (LTX_NAME_PATTERN.test(value)) {
        return true;
    }
    return isLtxV2CompatId(getLtxHostBridge().getCurrentModelCompatId());
};
