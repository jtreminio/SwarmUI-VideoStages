import type { Clip, IcLora } from "../types";
import {
    type GeneratedEntryMode,
    reconcileIncomingIcLoraDrives,
} from "./ltx2/icLoraDriveAvailability";
import * as ltx2IcLoraBehavior from "./ltx2/icLoraNormalization";

/** Pure architecture-owned authoring behavior with no DOM dependencies. */
export interface ArchitectureBehavior {
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
}

const ltx2Behavior: ArchitectureBehavior = {
    normalizeIcLoras: ltx2IcLoraBehavior.normalizeIcLoras,
    canonicalizeIcLoraFields: ltx2IcLoraBehavior.canonicalizeIcLoraFields,
    reconcileIncomingIcLoraDrives,
    hasSlotSourcedIcLora: ltx2IcLoraBehavior.hasSlotSourcedIcLora,
    isHdrFeature: ltx2IcLoraBehavior.isHdrFeature,
};

const behaviors = new Map<string, ArchitectureBehavior>([
    ["ltx2", ltx2Behavior],
]);

export const architectureBehavior = (
    architectureId: string,
): ArchitectureBehavior | null => behaviors.get(architectureId) ?? null;

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
