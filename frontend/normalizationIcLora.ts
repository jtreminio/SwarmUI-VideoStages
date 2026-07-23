import {
    CONTROLNET_SOURCE_OPTIONS,
    IC_LORA_ATTENTION_DEFAULT,
    IC_LORA_ATTENTION_MAX,
    IC_LORA_ATTENTION_MIN,
    IC_LORA_ATTENTION_STEP,
    IC_LORA_SOURCE_STAGE_INPUT,
    IC_LORA_SOURCE_UPLOAD,
    IC_LORA_STAGE_ALL,
    IC_LORA_STRENGTH_DEFAULT,
    IC_LORA_STRENGTH_MAX,
    IC_LORA_STRENGTH_MIN,
    IC_LORA_STRENGTH_STEP,
} from "./constants";
import { IC_LORA_PRESET_CUSTOM_ID } from "./icLoraPresets";
import { normalizeUploadedAudio } from "./normalizationMedia";
import { readProp, snapStrengthToStep } from "./normalizationShared";
import type { IcLora, IcLoraControlType } from "./types";
import { isRecord } from "./utils";

export const normalizeControlNetSource = (value: unknown): string => {
    const compact = `${value ?? ""}`.trim().replace(/\s+/g, "").toLowerCase();
    for (const option of CONTROLNET_SOURCE_OPTIONS) {
        if (option.replace(/\s+/g, "").toLowerCase() === compact) {
            return option;
        }
    }
    return CONTROLNET_SOURCE_OPTIONS[0];
};

export const defaultIcLora = (overrides: Partial<IcLora> = {}): IcLora => ({
    lora: "",
    preset: IC_LORA_PRESET_CUSTOM_ID,
    source: IC_LORA_SOURCE_UPLOAD,
    stage: IC_LORA_STAGE_ALL,
    strength: IC_LORA_STRENGTH_DEFAULT,
    attentionStrength: IC_LORA_ATTENTION_DEFAULT,
    controlType: "none",
    video: null,
    driveAudioRef: false,
    ...overrides,
});

export const normalizeControlNetLora = (value: unknown): string => {
    const raw = `${value ?? ""}`.trim();
    if (!raw) {
        return "";
    }
    const squeezed = raw.replace(/\s+/g, "").toLowerCase();
    if (squeezed === "(none)") {
        return "";
    }
    return raw;
};

export const normalizeIcLoraControlType = (
    value: unknown,
): IcLoraControlType => {
    const raw = `${value ?? ""}`.trim().toLowerCase();
    return raw === "canny" || raw === "depth" || raw === "normal"
        ? raw
        : "none";
};

const normalizeIcLoraSource = (value: unknown): string => {
    const compact = `${value ?? ""}`.trim().replace(/\s+/g, "").toLowerCase();
    if (!compact || compact === "upload") {
        return IC_LORA_SOURCE_UPLOAD;
    }
    if (compact === "stageinput") {
        return IC_LORA_SOURCE_STAGE_INPUT;
    }
    return normalizeControlNetSource(value);
};

const normalizeIcLoraStage = (value: unknown, stageCount: number): number => {
    if (value == null || `${value}`.trim() === "") {
        return IC_LORA_STAGE_ALL;
    }
    const stage = Math.trunc(Number(value));
    if (!Number.isFinite(stage) || stage < 0) {
        return IC_LORA_STAGE_ALL;
    }
    // A target beyond the clip's stage list (stale after stage deletion) would
    // be silently skipped by every stage the backend runs — heal it to "all".
    return stageCount > 0 && stage >= stageCount ? IC_LORA_STAGE_ALL : stage;
};

export const normalizeIcLora = (
    raw: unknown,
    stageCount: number = 0,
    sourcedClip = false,
): IcLora | null => {
    if (!isRecord(raw)) {
        return null;
    }
    const lora = normalizeControlNetLora(readProp(raw, "lora", "Lora"));
    if (!lora) {
        return null;
    }
    const preset = `${readProp(raw, "preset", "Preset") ?? ""}`.trim();
    const stage = normalizeIcLoraStage(
        readProp(raw, "stage", "Stage"),
        stageCount,
    );
    let source = normalizeIcLoraSource(readProp(raw, "source", "Source"));
    // Stage Input means "this stage's incoming frames" — meaningless below a
    // refine-stage target UNLESS a sourced clip provides the stage-0 input.
    if (source === IC_LORA_SOURCE_STAGE_INPUT && stage < 1 && !sourcedClip) {
        source = IC_LORA_SOURCE_UPLOAD;
    }
    return {
        lora,
        preset: preset || IC_LORA_PRESET_CUSTOM_ID,
        source,
        stage,
        strength: snapStrengthToStep(
            readProp(raw, "strength", "Strength"),
            IC_LORA_STRENGTH_DEFAULT,
            IC_LORA_STRENGTH_MIN,
            IC_LORA_STRENGTH_MAX,
            IC_LORA_STRENGTH_STEP,
        ),
        attentionStrength: snapStrengthToStep(
            readProp(raw, "attentionStrength", "AttentionStrength"),
            IC_LORA_ATTENTION_DEFAULT,
            IC_LORA_ATTENTION_MIN,
            IC_LORA_ATTENTION_MAX,
            IC_LORA_ATTENTION_STEP,
        ),
        controlType: normalizeIcLoraControlType(
            readProp(raw, "controlType", "ControlType"),
        ),
        video: normalizeUploadedAudio(readProp(raw, "video", "Video")),
        driveAudioRef: readProp(raw, "driveAudioRef", "DriveAudioRef") === true,
    };
};

/**
 * Reads the clip's IC-LoRA list, falling back to the legacy single-entry
 * `controlNetLora` + `controlNetSource` fields when no array is present.
 */
export const normalizeIcLoras = (
    rawClip: Record<string, unknown>,
    stageCount: number = 0,
    sourcedClip = false,
): IcLora[] => {
    const raw = readProp(rawClip, "icLoras", "IcLoras");
    if (Array.isArray(raw)) {
        const entries = raw
            .map((entry) => normalizeIcLora(entry, stageCount, sourcedClip))
            .filter((entry): entry is IcLora => entry !== null);
        if (entries.length > 0) {
            return entries;
        }
    }
    const legacyLora = normalizeControlNetLora(
        readProp(rawClip, "controlNetLora", "ControlNetLora"),
    );
    if (!legacyLora) {
        return [];
    }
    return [
        defaultIcLora({
            lora: legacyLora,
            source: normalizeControlNetSource(
                readProp(rawClip, "controlNetSource", "ControlNetSource"),
            ),
        }),
    ];
};

export const reconcileIcLoraStage = (
    entry: IcLora,
    sourcedClip = false,
): void => {
    if (
        entry.stage < 1 &&
        entry.source === IC_LORA_SOURCE_STAGE_INPUT &&
        !sourcedClip
    ) {
        entry.source = IC_LORA_SOURCE_UPLOAD;
    }
};

/** True when any entry is driven by a captured core "ControlNet N" branch. */
export const hasSlotSourcedIcLora = (icLoras: IcLora[]): boolean =>
    icLoras.some(
        (entry) =>
            entry.source !== IC_LORA_SOURCE_UPLOAD &&
            entry.source !== IC_LORA_SOURCE_STAGE_INPUT,
    );
