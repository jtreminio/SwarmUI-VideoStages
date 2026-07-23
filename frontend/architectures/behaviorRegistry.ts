import type { IcLora } from "../types";
import * as ltx2IcLoraBehavior from "./ltx2/icLoraNormalization";

/** Pure architecture-owned authoring behavior with no DOM dependencies. */
export interface ArchitectureBehavior {
    normalizeIcLoras(
        rawClip: Record<string, unknown>,
        stageCount: number,
        sourcedClip: boolean,
    ): IcLora[];
    reconcileIcLoraStage(entry: IcLora, sourcedClip: boolean): void;
    hasSlotSourcedIcLora(entries: IcLora[]): boolean;
    isHdrFeature(entry: IcLora): boolean;
}

const ltx2Behavior: ArchitectureBehavior = {
    normalizeIcLoras: ltx2IcLoraBehavior.normalizeIcLoras,
    reconcileIcLoraStage: ltx2IcLoraBehavior.reconcileIcLoraStage,
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

export const reconcileArchitectureIcLoraStage = (
    architectureId: string,
    entry: IcLora,
    sourcedClip: boolean,
): void => {
    architectureBehavior(architectureId)?.reconcileIcLoraStage(
        entry,
        sourcedClip,
    );
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
