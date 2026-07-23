import {
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
} from "../../icLoraAuthoring";
import { normalizeUploadedMedia } from "../../normalizationMedia";
import { snapValueToStep } from "../../normalizationShared";
import type { IcLora, IcLoraControlType } from "../../types";
import { isRecord } from "../../utils";
import {
    findIcLoraPreset,
    IC_LORA_PRESET_CUSTOM_ID,
    icLoraDriveMediaContract,
} from "./icLoraPresets";

const CONTROLNET_SOURCE_OPTIONS = [
    "ControlNet 1",
    "ControlNet 2",
    "ControlNet 3",
] as const;

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
    driveMedia: null,
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
    const lora = normalizeControlNetLora(raw.lora);
    if (!lora) {
        return null;
    }
    const preset = `${raw.preset ?? ""}`.trim();
    const normalizedPreset = preset || IC_LORA_PRESET_CUSTOM_ID;
    const driveContract = icLoraDriveMediaContract(
        findIcLoraPreset(normalizedPreset),
    );
    const consumesAudio = driveContract.consumes === "audio";
    const normalizedDriveMedia = normalizeUploadedMedia(raw.driveMedia);
    const driveMedia =
        normalizedDriveMedia &&
        driveContract.acceptedKinds.some((kind) =>
            normalizedDriveMedia.data.startsWith(`data:${kind}/`),
        )
            ? normalizedDriveMedia
            : null;
    const stage = normalizeIcLoraStage(raw.stage, stageCount);
    let source = normalizeIcLoraSource(raw.source);
    // Stage Input means "this stage's incoming frames" — meaningless below a
    // refine-stage target UNLESS a sourced clip provides the stage-0 input.
    if (source === IC_LORA_SOURCE_STAGE_INPUT && stage < 1 && !sourcedClip) {
        source = IC_LORA_SOURCE_UPLOAD;
    }
    // LipDub's Drive Media is audio-only conditioning. Its visuals come from
    // the ordinary stage input, so its legacy visual-source setting is never
    // meaningful or authorable.
    if (consumesAudio) {
        source = IC_LORA_SOURCE_UPLOAD;
    }
    return {
        lora,
        preset: normalizedPreset,
        source,
        stage,
        strength: snapValueToStep(
            raw.strength,
            IC_LORA_STRENGTH_DEFAULT,
            IC_LORA_STRENGTH_MIN,
            IC_LORA_STRENGTH_MAX,
            IC_LORA_STRENGTH_STEP,
        ),
        attentionStrength: snapValueToStep(
            raw.attentionStrength,
            IC_LORA_ATTENTION_DEFAULT,
            IC_LORA_ATTENTION_MIN,
            IC_LORA_ATTENTION_MAX,
            IC_LORA_ATTENTION_STEP,
        ),
        controlType: consumesAudio
            ? "none"
            : normalizeIcLoraControlType(raw.controlType),
        driveMedia,
    };
};

/** Reads the clip's canonical IC-LoRA list. */
export const normalizeIcLoras = (
    rawClip: Record<string, unknown>,
    stageCount: number = 0,
    sourcedClip = false,
): IcLora[] => {
    if (!Array.isArray(rawClip.icLoras)) {
        return [];
    }
    return rawClip.icLoras
        .map((entry) => normalizeIcLora(entry, stageCount, sourcedClip))
        .filter((entry): entry is IcLora => entry !== null);
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

/** LTX-specific recognition of its HDR preset and weight naming convention. */
export const isHdrFeature = (entry: IcLora): boolean =>
    `${entry.preset ?? ""}`.trim().toLowerCase() === "hdr" ||
    `${entry.lora ?? ""}`.toLowerCase().includes("hdr");

/** True when any entry is driven by a captured core "ControlNet N" branch. */
export const hasSlotSourcedIcLora = (icLoras: IcLora[]): boolean =>
    icLoras.some(
        (entry) =>
            entry.source !== IC_LORA_SOURCE_UPLOAD &&
            entry.source !== IC_LORA_SOURCE_STAGE_INPUT,
    );
