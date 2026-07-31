import {
    canRequestHostWs,
    refreshHostParameters,
    requestHostWs,
} from "../../host/swarmUiAdapters";
import type { IcLora } from "../../types";
import {
    findIcLoraPreset,
    IC_LORA_AUTO,
    type IcLoraPreset,
    icLoraAutoModelName,
    icLoraLegacyAutoModelName,
} from "./icLoraPresets";

// Fulfills "[AUTO]" IC-LoRA entries: kicks off the extension's VideoStagesDownloadIcLoraWS to fetch
// the preset's safetensors into LTX-2/IC-LoRA/. Download state is tracked per preset id
// (module-level, shared by every entry using that preset); progress repaints only the tagged hint
// elements, not the full strip.

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

/** The installed LoRA fulfilling this preset's [AUTO] entry, or null. */
const installedAutoWeights = (
    preset: IcLoraPreset,
    installedLoras: readonly string[],
): string | null => {
    const wanted = [
        icLoraAutoModelName(preset).toLowerCase(),
        icLoraLegacyAutoModelName(preset).toLowerCase(),
    ];
    return (
        installedLoras.find((name) =>
            wanted.includes(`${name}`.toLowerCase()),
        ) ?? null
    );
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
    refreshHostParameters();
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
        installedAutoWeights(preset, installedLoras) ||
        statuses.has(preset.id)
    ) {
        return;
    }
    if (!canRequestHostWs()) {
        statuses.set(preset.id, {
            state: "error",
            message: "Model downloader is unavailable.",
        });
        return;
    }
    statuses.set(preset.id, { state: "downloading", percent: 0 });
    requestHostWs(
        "VideoStagesDownloadIcLoraWS",
        { presetId: preset.id },
        (data) => {
            // overall_percent is the workflow-step indicator (constant 0.2 while
            // transferring); current_percent is the live transfer progress.
            if (typeof data?.current_percent === "number") {
                setStatus(preset, {
                    state: "downloading",
                    percent: data.current_percent,
                });
            } else if (data?.success) {
                finish(preset, onSettled);
            }
        },
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
    const installed = installedAutoWeights(preset, installedLoras);
    if (installed) {
        return `Using ${installed}.`;
    }
    const status = statuses.get(preset.id);
    return status
        ? statusTextFor(preset, status)
        : "Preparing preset weights download…";
};
