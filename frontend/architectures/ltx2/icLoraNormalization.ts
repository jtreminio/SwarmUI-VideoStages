import {
    IC_LORA_ATTENTION_DEFAULT,
    IC_LORA_ATTENTION_MAX,
    IC_LORA_ATTENTION_MIN,
    IC_LORA_ATTENTION_STEP,
    IC_LORA_SOURCE_INCOMING,
    IC_LORA_SOURCE_UPLOAD,
    IC_LORA_STAGE_ALL,
    IC_LORA_STRENGTH_DEFAULT,
    IC_LORA_STRENGTH_MAX,
    IC_LORA_STRENGTH_MIN,
    IC_LORA_STRENGTH_STEP,
} from "../../icLoraAuthoring";
import { normalizeUploadedMedia } from "../../normalizationMedia";
import { snapValueToStep } from "../../normalizationShared";
import type {
    IcLora,
    IcLoraControlType,
    IcLoraDriveData,
    IcLoraDriveMediaKind,
} from "../../types";
import { isRecord } from "../../utils";
import {
    findIcLoraPreset,
    IC_LORA_AUTO,
    IC_LORA_DEFAULT_PRESET_ID,
    IC_LORA_PRESET_CUSTOM_ID,
    icLoraDriveMediaContractForData,
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
    driveSource: IC_LORA_SOURCE_UPLOAD,
    driveData: "visual",
    driveMediaKinds: ["image", "video"],
    stage: IC_LORA_STAGE_ALL,
    strength: IC_LORA_STRENGTH_DEFAULT,
    attentionStrength: IC_LORA_ATTENTION_DEFAULT,
    controlType: "none",
    hdr: false,
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

const normalizeIcLoraDriveSource = (value: unknown): string => {
    const compact = `${value ?? ""}`.trim().replace(/\s+/g, "").toLowerCase();
    if (!compact || compact === "upload") {
        return IC_LORA_SOURCE_UPLOAD;
    }
    if (compact === "incoming" || compact === "stageinput") {
        return IC_LORA_SOURCE_INCOMING;
    }
    return normalizeControlNetSource(value);
};

const normalizeIcLoraDriveData = (value: unknown): IcLoraDriveData => {
    const compact = `${value ?? ""}`.trim().toLowerCase();
    return compact === "none" || compact === "audio" ? compact : "visual";
};

const mediaKindsForData = (
    driveData: IcLoraDriveData,
): readonly IcLoraDriveMediaKind[] =>
    icLoraDriveMediaContractForData(driveData).acceptedKinds;

/** Preserves an explicit declarative contract while dropping incompatible kinds. */
export const normalizeIcLoraDriveMediaKinds = (
    value: unknown,
    driveData: IcLoraDriveData,
): IcLoraDriveMediaKind[] => {
    const allowed = mediaKindsForData(driveData);
    if (!Array.isArray(value)) {
        return [...allowed];
    }
    const kinds: IcLoraDriveMediaKind[] = [];
    for (const rawKind of value) {
        const kind = `${rawKind ?? ""}`.trim().toLowerCase();
        if (
            (kind === "image" || kind === "video" || kind === "audio") &&
            allowed.includes(kind) &&
            !kinds.includes(kind)
        ) {
            kinds.push(kind);
        }
    }
    return kinds;
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
    _sourcedClip = false,
): IcLora | null => {
    if (!isRecord(raw)) {
        return null;
    }
    const lora = normalizeControlNetLora(raw.lora);
    if (!lora) {
        return null;
    }
    const preset = `${raw.preset ?? ""}`.trim();
    const repairsLegacyCustomAuto =
        lora === IC_LORA_AUTO &&
        (!preset || preset === IC_LORA_PRESET_CUSTOM_ID);
    const normalizedPreset = repairsLegacyCustomAuto
        ? IC_LORA_DEFAULT_PRESET_ID
        : preset || IC_LORA_PRESET_CUSTOM_ID;
    const repairedPreset = repairsLegacyCustomAuto
        ? findIcLoraPreset(normalizedPreset)
        : null;
    const driveData = normalizeIcLoraDriveData(raw.driveData);
    const driveMediaKinds = normalizeIcLoraDriveMediaKinds(
        raw.driveMediaKinds,
        driveData,
    );
    const normalizedDriveMedia = normalizeUploadedMedia(raw.driveMedia);
    let driveSource = normalizeIcLoraDriveSource(raw.driveSource);
    const driveMedia =
        driveSource === IC_LORA_SOURCE_UPLOAD &&
        driveData !== "none" &&
        normalizedDriveMedia &&
        driveMediaKinds.some((kind) =>
            normalizedDriveMedia.data.startsWith(`data:${kind}/`),
        )
            ? normalizedDriveMedia
            : null;
    const stage = normalizeIcLoraStage(raw.stage, stageCount);
    if (driveData === "none") {
        driveSource = IC_LORA_SOURCE_UPLOAD;
    }
    return {
        lora,
        preset: normalizedPreset,
        driveSource,
        driveData,
        driveMediaKinds,
        stage,
        strength: repairsLegacyCustomAuto
            ? (repairedPreset?.strength ?? IC_LORA_STRENGTH_DEFAULT)
            : snapValueToStep(
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
        controlType:
            driveData !== "visual"
                ? "none"
                : repairsLegacyCustomAuto
                  ? (repairedPreset?.controlType ?? "none")
                  : normalizeIcLoraControlType(raw.controlType),
        // Documents authored before the flag existed carry only the preset id; the preset table
        // (not a name match) seeds the intent, so those documents keep working.
        hdr:
            typeof raw.hdr === "boolean"
                ? raw.hdr
                : (findIcLoraPreset(normalizedPreset)?.hdr ?? false),
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

export const canonicalizeIcLoraFields = (entry: IcLora): void => {
    if (entry.driveData === "none") {
        entry.driveSource = IC_LORA_SOURCE_UPLOAD;
        entry.driveMedia = null;
    }
    entry.driveMediaKinds = normalizeIcLoraDriveMediaKinds(
        entry.driveMediaKinds,
        entry.driveData,
    );
};

/** Reads the persisted typed HDR contract; never the preset or LoRA name. */
export const isHdrFeature = (entry: IcLora): boolean => entry.hdr === true;

/** True when any entry is driven by a captured core "ControlNet N" branch. */
export const hasSlotSourcedIcLora = (icLoras: IcLora[]): boolean =>
    icLoras.some(
        (entry) =>
            entry.driveSource !== IC_LORA_SOURCE_UPLOAD &&
            entry.driveSource !== IC_LORA_SOURCE_INCOMING,
    );
