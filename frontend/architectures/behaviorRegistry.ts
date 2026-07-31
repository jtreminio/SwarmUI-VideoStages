import { ROOT_DIMENSION_STEP } from "../constants";
import type { Clip, IcLora } from "../types";
import { resolvedClipArchitectureId } from "./clipIdentity";
import { ltx2DimensionMultiple } from "./ltx2/dimensionPolicy";
import type { GeneratedEntryMode } from "./ltx2/icLoraDriveAvailability";
import { reconcileIncomingIcLoraDrives } from "./ltx2/icLoraDriveAvailability";
import * as icLoraNormalization from "./ltx2/icLoraNormalization";
import { icLoraDisplayName } from "./ltx2/icLoraPresets";
import { LTX2_ARCHITECTURE_ID } from "./ltx2/identity";
import type { ArchitectureModelCatalog } from "./types";

const isLtx2 = (architectureId: string): boolean =>
    architectureId === LTX2_ARCHITECTURE_ID;

export const architectureDimensionMultiple = (
    clip: Clip,
    architectureId: string,
): number => {
    const requested = isLtx2(architectureId)
        ? ltx2DimensionMultiple(clip)
        : ROOT_DIMENSION_STEP;
    if (!Number.isFinite(requested)) {
        return ROOT_DIMENSION_STEP;
    }
    return Math.max(
        ROOT_DIMENSION_STEP,
        Math.ceil(requested / ROOT_DIMENSION_STEP) * ROOT_DIMENSION_STEP,
    );
};

export interface IcLoraNormalizationOptions {
    /**
     * Decode the persisted LTX-owned carrier even when another architecture
     * executes the clip. The data remains dormant; this does not grant that
     * architecture IC-LoRA behavior.
     */
    preserveDormantLtx?: boolean;
}

export const normalizeArchitectureIcLoras = (
    architectureId: string,
    rawClip: Record<string, unknown>,
    stageCount: number,
    sourcedClip: boolean,
    options: IcLoraNormalizationOptions = {},
): IcLora[] => {
    if (isLtx2(architectureId)) {
        return icLoraNormalization.normalizeIcLoras(
            rawClip,
            stageCount,
            sourcedClip,
        );
    }
    return options.preserveDormantLtx === true &&
        Array.isArray(rawClip.icLoras) &&
        rawClip.icLoras.length > 0
        ? icLoraNormalization.normalizeIcLoras(rawClip, stageCount, sourcedClip)
        : [];
};

export const canonicalizeArchitectureIcLoraFields = (
    architectureId: string,
    entry: IcLora,
): void => {
    if (isLtx2(architectureId)) {
        icLoraNormalization.canonicalizeIcLoraFields(entry);
    }
};

/** Lets each architecture repair graph-relative IC-LoRA state after graph edits. */
export const reconcileArchitectureIncomingIcLoraDrives = (
    clips: Clip[],
    generatedEntryMode: GeneratedEntryMode,
    catalog: ArchitectureModelCatalog | null,
): boolean => {
    let changed = false;
    clips.forEach((clip, clipIdx) => {
        const architectureId = resolvedClipArchitectureId(clip, catalog) ?? "";
        changed =
            (isLtx2(architectureId) &&
                reconcileIncomingIcLoraDrives(
                    clips,
                    clipIdx,
                    generatedEntryMode,
                )) ||
            changed;
    });
    return changed;
};

/** Repairs graph-relative state only for one clip whose behavior was activated. */
export const reconcileClipArchitectureIncomingIcLoraDrives = (
    clips: Clip[],
    clipIdx: number,
    generatedEntryMode: GeneratedEntryMode,
    catalog: ArchitectureModelCatalog | null,
): boolean => {
    const clip = clips[clipIdx];
    if (!clip) return false;
    const architectureId = resolvedClipArchitectureId(clip, catalog) ?? "";
    return (
        isLtx2(architectureId) &&
        reconcileIncomingIcLoraDrives(clips, clipIdx, generatedEntryMode)
    );
};

export const hasArchitectureSlotSourcedIcLora = (
    architectureId: string,
    entries: IcLora[],
): boolean =>
    isLtx2(architectureId) && icLoraNormalization.hasSlotSourcedIcLora(entries);

export const architectureIcLoraDisplayName = (
    architectureId: string,
    entry: IcLora,
): string => (isLtx2(architectureId) ? icLoraDisplayName(entry) : entry.lora);
