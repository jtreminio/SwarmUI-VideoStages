import { IC_LORA_AUTO } from "./constants";
import {
    findIcLoraPreset,
    type IcLoraPreset,
    icLoraAutoModelName,
} from "./icLoraPresets";
import type { IcLora } from "./types";

// Fulfills "[AUTO]" IC-LoRA entries: when an entry pairs the [AUTO] lora choice with a preset
// whose weights aren't installed, this kicks off SwarmUI's DoModelDownloadWS to fetch the
// preset's safetensors into LTX-2/IC-LoRA/<preset id>. Download state is tracked per preset id
// (module-level, shared by every entry using that preset); progress repaints only the tagged
// hint elements so the websocket stream never forces a full strip re-render.

/** Attribute the detail strip puts on an entry's auto-status hint element (value = preset id). */
export const IC_LORA_AUTO_HINT_ATTR = "data-vst-iclora-auto";

export type IcLoraAutoStatus =
    | { state: "downloading"; percent: number }
    | { state: "done" }
    | { state: "error"; message: string };

const statuses = new Map<string, IcLoraAutoStatus>();

/** Drops all tracked download state (tests only). */
export const resetIcLoraAutoDownloads = (): void => {
    statuses.clear();
};

/** Clears a failed download so the next ensure call retries it. */
export const clearIcLoraAutoFailure = (presetId: string): void => {
    if (statuses.get(`${presetId ?? ""}`.trim())?.state === "error") {
        statuses.delete(`${presetId ?? ""}`.trim());
    }
};

const hasAutoWeights = (
    preset: IcLoraPreset,
    installedLoras: readonly string[],
): boolean => {
    const wanted = icLoraAutoModelName(preset).toLowerCase();
    return installedLoras.some((name) => `${name}`.toLowerCase() === wanted);
};

const statusTextFor = (
    preset: IcLoraPreset,
    status: IcLoraAutoStatus,
): string => {
    switch (status.state) {
        case "downloading":
            return `Downloading ${preset.displayName} weights… ${Math.round(status.percent * 100)}%`;
        case "done":
            return `Downloaded to ${icLoraAutoModelName(preset)}.`;
        case "error":
            return `Download failed: ${status.message} Reselect the preset to retry.`;
    }
};

const setStatus = (preset: IcLoraPreset, status: IcLoraAutoStatus): void => {
    statuses.set(preset.id, status);
    const text = statusTextFor(preset, status);
    document
        .querySelectorAll(`[${IC_LORA_AUTO_HINT_ATTR}="${preset.id}"]`)
        .forEach((el) => {
            el.textContent = text;
        });
};

const finish = (preset: IcLoraPreset, onSettled: () => void): void => {
    setStatus(preset, { state: "done" });
    // DoModelDownloadWS does not refresh model lists itself; this pulls the new file into the
    // core LoRA param (and thus this strip's dropdown values) and into the backend model set.
    if (typeof refreshParameterValues === "function") {
        refreshParameterValues(true);
    }
    onSettled();
};

/**
 * Starts the preset-weights download for an [AUTO] entry when needed. No-op unless the entry's
 * lora is [AUTO] with a known preset whose weights are neither installed nor already being
 * fetched. A failed download stays failed until clearIcLoraAutoFailure (no retry loops).
 * onSettled runs after terminal states (success, already-exists, error) — not per progress tick.
 */
export const ensureIcLoraAutoWeights = (
    entry: IcLora,
    installedLoras: readonly string[],
    onSettled: () => void,
): void => {
    if (entry.lora !== IC_LORA_AUTO) {
        return;
    }
    const preset = findIcLoraPreset(entry.preset);
    if (
        !preset ||
        hasAutoWeights(preset, installedLoras) ||
        statuses.has(preset.id)
    ) {
        return;
    }
    if (typeof makeWSRequest !== "function") {
        statuses.set(preset.id, {
            state: "error",
            message: "Model downloader is unavailable.",
        });
        return;
    }
    statuses.set(preset.id, { state: "downloading", percent: 0 });
    makeWSRequest(
        "DoModelDownloadWS",
        {
            url: preset.weightsUrl,
            type: "LoRA",
            name: icLoraAutoModelName(preset),
        },
        (data) => {
            // overall_percent is the workflow-step indicator (a constant 0.2
            // while transferring); the live transfer progress is current_percent.
            if (typeof data?.current_percent === "number") {
                setStatus(preset, {
                    state: "downloading",
                    percent: data.current_percent,
                });
            } else if (data?.success) {
                finish(preset, onSettled);
            }
        },
        0,
        (error) => {
            if (`${error}` === "Model at that save path already exists.") {
                finish(preset, onSettled);
                return;
            }
            setStatus(preset, { state: "error", message: `${error}` });
            onSettled();
        },
    );
};

/** The status line for an entry's [AUTO] state; "" when the entry isn't [AUTO]. */
export const icLoraAutoHint = (
    entry: IcLora,
    installedLoras: readonly string[],
): string => {
    if (entry.lora !== IC_LORA_AUTO) {
        return "";
    }
    const preset = findIcLoraPreset(entry.preset);
    if (!preset) {
        return "[AUTO] needs a preset — pick one to download its weights.";
    }
    if (hasAutoWeights(preset, installedLoras)) {
        return `Using ${icLoraAutoModelName(preset)}.`;
    }
    const status = statuses.get(preset.id);
    return status
        ? statusTextFor(preset, status)
        : "Preparing preset weights download…";
};
