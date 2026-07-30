import { activeStageCount } from "../clipSemantics";
import { ROOT_DIMENSION_STEP } from "../constants";
import type { Clip, IcLora } from "../types";
import { ltx2Behavior } from "./ltx2/behavior";
import type { GeneratedEntryMode } from "./ltx2/icLoraDriveAvailability";
import { LTX2_ARCHITECTURE_ID } from "./ltx2/identity";

/** Pure architecture-owned authoring behavior with no DOM dependencies. */
export interface ArchitectureBehavior {
    /**
     * Architecture-owned grid requirement for this clip. The document layer
     * always applies its global /32 grid in addition to this requirement.
     */
    dimensionMultiple(clip: Clip): number;
    normalizeIcLoras(
        rawClip: Record<string, unknown>,
        stageCount: number,
        sourcedClip: boolean,
    ): IcLora[];
    canonicalizeIcLoraFields(entry: IcLora): void;
    reconcileIncomingIcLoraDrives(
        clips: Clip[],
        clipIdx: number,
        generatedEntryMode: GeneratedEntryMode,
    ): boolean;
    hasSlotSourcedIcLora(entries: IcLora[]): boolean;
    isHdrFeature(entry: IcLora): boolean;
    icLoraDisplayName(entry: IcLora): string;
}

const behaviors = new Map<string, ArchitectureBehavior>([
    [LTX2_ARCHITECTURE_ID, ltx2Behavior],
]);

export const architectureBehavior = (
    architectureId: string,
): ArchitectureBehavior | null => behaviors.get(architectureId) ?? null;

export const architectureDimensionMultiple = (clip: Clip): number => {
    const requested =
        architectureBehavior(clip.architecture)?.dimensionMultiple(clip) ??
        ROOT_DIMENSION_STEP;
    if (!Number.isFinite(requested)) {
        return ROOT_DIMENSION_STEP;
    }
    return Math.max(
        ROOT_DIMENSION_STEP,
        Math.ceil(requested / ROOT_DIMENSION_STEP) * ROOT_DIMENSION_STEP,
    );
};

/**
 * The LTX fallback exists only to preserve already-authored IC values for
 * removal when a source-only or unknown clip has no active behavior owner.
 */
export const normalizeArchitectureIcLoras = (
    architectureId: string,
    rawClip: Record<string, unknown>,
    stageCount: number,
    sourcedClip: boolean,
    allowPersistedLtxFallback = false,
): IcLora[] => {
    const behavior = architectureBehavior(architectureId);
    if (behavior) {
        return behavior.normalizeIcLoras(rawClip, stageCount, sourcedClip);
    }
    return allowPersistedLtxFallback &&
        Array.isArray(rawClip.icLoras) &&
        rawClip.icLoras.length > 0
        ? ltx2Behavior.normalizeIcLoras(rawClip, stageCount, sourcedClip)
        : [];
};

export const canonicalizeArchitectureIcLoraFields = (
    architectureId: string,
    entry: IcLora,
): void => {
    architectureBehavior(architectureId)?.canonicalizeIcLoraFields(entry);
};

/** Lets each architecture repair graph-relative IC-LoRA state after graph edits. */
export const reconcileArchitectureIncomingIcLoraDrives = (
    clips: Clip[],
    generatedEntryMode: GeneratedEntryMode,
): boolean => {
    let changed = false;
    clips.forEach((clip, clipIdx) => {
        changed =
            architectureBehavior(
                clip.architecture,
            )?.reconcileIncomingIcLoraDrives(
                clips,
                clipIdx,
                generatedEntryMode,
            ) || changed;
    });
    return changed;
};

export const hasArchitectureSlotSourcedIcLora = (
    architectureId: string,
    entries: IcLora[],
): boolean =>
    architectureBehavior(architectureId)?.hasSlotSourcedIcLora(entries) ??
    false;

export const isArchitectureHdrFeature = (
    architectureId: string,
    entry: IcLora,
): boolean =>
    architectureBehavior(architectureId)?.isHdrFeature(entry) ?? false;

export const architectureIcLoraDisplayName = (
    architectureId: string,
    entry: IcLora,
): string =>
    architectureBehavior(architectureId)?.icLoraDisplayName(entry) ??
    entry.lora;

/** True when the clip has an HDR IC-LoRA bound to at least one active stage. */
export const clipHasActiveHdrForArchitecture = (
    clip: Clip,
    architectureId: string,
): boolean =>
    clip.icLoras.some(
        (entry) =>
            isArchitectureHdrFeature(architectureId, entry) &&
            clip.stages
                .slice(0, activeStageCount(clip))
                .some(
                    (_stage, rawStageIdx) =>
                        entry.stage < 0 || entry.stage === rawStageIdx,
                ),
    );

export const clipHasActiveHdr = (clip: Clip): boolean =>
    clipHasActiveHdrForArchitecture(clip, clip.architecture);
