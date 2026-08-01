import { activeStageCount } from "../clipSemantics";
import type { Clip } from "../types";
import { architectureDescriptor, modelCatalogEntry } from "./catalogQueries";
import { effectiveClipCapabilities } from "./modelCapabilities";
import {
    architectureFeatureSupport,
    upscaleModeForMethod,
} from "./policy/featureValues";
import type {
    ArchitectureCatalogEntryDto,
    ArchitectureModelCatalog,
    ArchitectureModelEntry,
} from "./types";

/** Mirrors the largest temporal grid the backend can represent in a C# Int32. */
export const MAX_FRAME_GRID = 2_147_483_647;

export type FrameGridResolution =
    | { status: "resolved"; frameGrid: number }
    | { status: "not-applicable" }
    | { status: "unknown" }
    | { status: "conflict" };

const greatestCommonDivisor = (left: number, right: number): number => {
    while (right !== 0) {
        [left, right] = [right, left % right];
    }
    return left;
};

/** Smallest positive grid satisfying every resolved active-stage handler. */
export const resolveCompatibleFrameGrid = (
    frameGrids: readonly number[],
): FrameGridResolution => {
    let compatible = 1;
    for (const raw of frameGrids) {
        const grid = Number(raw);
        if (!Number.isInteger(grid) || grid < 1 || grid > MAX_FRAME_GRID) {
            return { status: "conflict" };
        }
        const next =
            (compatible / greatestCommonDivisor(compatible, grid)) * grid;
        if (!Number.isSafeInteger(next) || next > MAX_FRAME_GRID) {
            return { status: "conflict" };
        }
        compatible = next;
    }
    return { status: "resolved", frameGrid: compatible };
};

export const resolveFrameGridForModelLookup = (
    models: readonly string[],
    frameGridForModel: (model: string) => number | null,
): FrameGridResolution => {
    if (models.length === 0) {
        return { status: "not-applicable" };
    }
    const grids = models.map(frameGridForModel);
    return grids.some((grid) => grid === null)
        ? { status: "unknown" }
        : resolveCompatibleFrameGrid(grids as number[]);
};

/** Neutral numeric projection for non-mutating preview geometry. */
export const frameGridForModelLookup = (
    models: readonly string[],
    frameGridForModel: (model: string) => number | null,
): number => {
    const resolution = resolveFrameGridForModelLookup(
        models,
        frameGridForModel,
    );
    return resolution.status === "resolved" ? resolution.frameGrid : 1;
};

/**
 * Unknown model facts deliberately produce the neutral grid. Backend admission will explain an
 * unresolved stage; frontend duration math must not guess another architecture's policy.
 */
export const frameGridForModels = (
    models: readonly string[],
    catalog: ArchitectureModelCatalog | null,
): number => {
    if (!catalog || models.length === 0) {
        return 1;
    }
    return frameGridForModelLookup(
        models,
        (model) => modelCatalogEntry(catalog, model)?.frameGrid ?? null,
    );
};

type TemporalClip = Pick<Clip, "stages"> &
    Partial<
        Pick<
            Clip,
            | "initVideo"
            | "retake"
            | "clipLengthFromAudio"
            | "clipLengthFromControlNet"
        >
    >;

const effectiveGridModels = (
    clip: TemporalClip,
    modelForName: (model: string) => ArchitectureModelEntry | undefined,
    architectureForId: (
        architectureId: string,
    ) => ArchitectureCatalogEntryDto | undefined,
): string[] => {
    const stages = clip.stages.slice(0, activeStageCount(clip));
    if (stages.length === 0) {
        return [];
    }
    const firstModel = modelForName(stages[0].model);
    const clipDescriptor = firstModel?.architectureId
        ? architectureForId(firstModel.architectureId)
        : undefined;
    const clipCapabilities = clipDescriptor
        ? effectiveClipCapabilities(clip, clipDescriptor, modelForName)
        : null;
    const retakeCanExecute =
        clip.retake !== null &&
        clip.retake !== undefined &&
        clip.initVideo != null &&
        (!clipDescriptor ||
            !clipCapabilities ||
            architectureFeatureSupport("retake", {
                capabilities: clipCapabilities,
            }));

    return stages
        .filter((stage, stageIndex) => {
            if (stageIndex === 0 && clip.initVideo == null) {
                return true;
            }
            if (
                stage.control > 0 ||
                (stageIndex === stages.length - 1 && retakeCanExecute)
            ) {
                return true;
            }
            const upscaleMode = upscaleModeForMethod(stage.upscaleMethod ?? "");
            if (
                (stage.upscale ?? 1) === 1 ||
                (upscaleMode !== "latent" && upscaleMode !== "latent-model")
            ) {
                return false;
            }
            return true;
        })
        .map((stage) => stage.model);
};

export const resolveClipFrameGridForLookup = (
    clip: TemporalClip,
    modelForName: (model: string) => ArchitectureModelEntry | undefined,
    architectureForId: (
        architectureId: string,
    ) => ArchitectureCatalogEntryDto | undefined,
): FrameGridResolution => {
    const activeStages = clip.stages.slice(0, activeStageCount(clip));
    if (activeStages.length === 0) {
        return { status: "not-applicable" };
    }
    const resolvedAuthoredModels = clip.stages.map((stage) =>
        modelForName(stage.model),
    );
    if (
        resolvedAuthoredModels.some(
            (model) =>
                !model?.architectureId ||
                !model.modelProfileId ||
                !model.compatibilityClassId,
        )
    ) {
        return { status: "unknown" };
    }
    const firstModel = resolvedAuthoredModels[0] as ArchitectureModelEntry;
    const descriptor = architectureForId(firstModel.architectureId as string);
    if (
        !descriptor ||
        resolvedAuthoredModels.some(
            (model) =>
                model?.architectureId !== firstModel.architectureId ||
                model.compatibilityClassId !== firstModel.compatibilityClassId,
        ) ||
        activeStages.some(
            (stage) =>
                (stage.upscale ?? 1) !== 1 &&
                upscaleModeForMethod(stage.upscaleMethod ?? "") ===
                    "unsupported",
        )
    ) {
        return { status: "unknown" };
    }
    const capabilities = effectiveClipCapabilities(
        clip,
        descriptor,
        modelForName,
    );
    if (!capabilities) {
        return { status: "unknown" };
    }
    const supportScope = { capabilities };
    if (
        (clip.clipLengthFromAudio === true &&
            architectureFeatureSupport("audioDerivedDuration", supportScope)) ||
        (clip.clipLengthFromControlNet === true &&
            architectureFeatureSupport(
                "controlSignalDerivedDuration",
                supportScope,
            ))
    ) {
        return { status: "not-applicable" };
    }
    const models = effectiveGridModels(clip, modelForName, architectureForId);
    return resolveFrameGridForModelLookup(
        models,
        (model) => modelForName(model)?.frameGrid ?? null,
    );
};

export const resolveClipFrameGrid = (
    clip: TemporalClip,
    catalog: ArchitectureModelCatalog | null,
): FrameGridResolution => {
    if (!catalog) {
        const activeCount = activeStageCount(clip);
        return activeCount === 0
            ? { status: "not-applicable" }
            : { status: "unknown" };
    }
    return resolveClipFrameGridForLookup(
        clip,
        (model) => modelCatalogEntry(catalog, model) ?? undefined,
        (architectureId) =>
            architectureDescriptor(catalog, architectureId) ?? undefined,
    );
};

export const resolvedClipFrameGrid = (
    clip: TemporalClip,
    catalog: ArchitectureModelCatalog | null,
): number => {
    const resolution = resolveClipFrameGrid(clip, catalog);
    return resolution.status === "resolved" ? resolution.frameGrid : 1;
};
