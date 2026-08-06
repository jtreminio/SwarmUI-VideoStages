import {
    canUseClipLengthFromAudio,
    isAllowedAudioSource,
} from "../audioSource";
import { activeStageCount } from "../clipSemantics";
import { type FrameGridSpec, NEUTRAL_FRAME_GRID } from "../renderUtils";
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
    | ({ status: "resolved" } & FrameGridSpec)
    | { status: "not-applicable" }
    | { status: "unknown" }
    | { status: "conflict" };

const greatestCommonDivisor = (left: number, right: number): number => {
    while (right !== 0) {
        [left, right] = [right, left % right];
    }
    return left;
};

/**
 * Smallest positive grid satisfying every resolved active-stage handler. Every stage in a clip
 * shares one architecture, so a disagreeing grid origin is a conflict rather than a merge.
 */
export const resolveCompatibleFrameGrid = (
    specs: readonly FrameGridSpec[],
): FrameGridResolution => {
    let compatible = 1;
    let origin: number | null = null;
    for (const raw of specs) {
        const grid = Number(raw.frameGrid);
        const gridOrigin = Number(raw.frameGridOrigin);
        if (
            !Number.isInteger(grid) ||
            grid < 1 ||
            grid > MAX_FRAME_GRID ||
            !Number.isInteger(gridOrigin) ||
            gridOrigin < 1 ||
            gridOrigin > grid ||
            (origin !== null && origin !== gridOrigin)
        ) {
            return { status: "conflict" };
        }
        origin = gridOrigin;
        const next =
            (compatible / greatestCommonDivisor(compatible, grid)) * grid;
        if (!Number.isSafeInteger(next) || next > MAX_FRAME_GRID) {
            return { status: "conflict" };
        }
        compatible = next;
    }
    return {
        status: "resolved",
        frameGrid: compatible,
        frameGridOrigin: origin ?? 1,
    };
};

export const resolveFrameGridForModelLookup = (
    models: readonly string[],
    frameGridForModel: (model: string) => FrameGridSpec | null,
): FrameGridResolution => {
    if (models.length === 0) {
        return { status: "not-applicable" };
    }
    const specs = models.map(frameGridForModel);
    return specs.some((spec) => spec === null)
        ? { status: "unknown" }
        : resolveCompatibleFrameGrid(specs as FrameGridSpec[]);
};

type TemporalClip = Pick<Clip, "stages"> &
    Partial<
        Pick<
            Clip,
            | "initVideo"
            | "retake"
            | "audioSource"
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
            architectureFeatureSupport("retake", clipCapabilities));

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
            return (
                clipCapabilities === null ||
                architectureFeatureSupport(
                    upscaleMode === "latent"
                        ? "latentUpscale"
                        : "latentModelUpscale",
                    clipCapabilities,
                )
            );
        })
        .map((stage) => stage.model);
};

export const clipHasGenerationStageForLookup = (
    clip: TemporalClip,
    modelForName: (model: string) => ArchitectureModelEntry | undefined,
    architectureForId: (
        architectureId: string,
    ) => ArchitectureCatalogEntryDto | undefined,
): boolean =>
    effectiveGridModels(clip, modelForName, architectureForId).length > 0;

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
    // Mirrors EffectiveVideoRequestProjector: only an architecture that declares the feature
    // derives the length at runtime, and audio only from a source that can supply one. The
    // ControlNet half deliberately has no source check — the backend falls back downstream.
    if (
        (clip.clipLengthFromAudio === true &&
            canUseClipLengthFromAudio(clip.audioSource ?? "") &&
            isAllowedAudioSource(
                capabilities.audioSourceKinds,
                clip.audioSource ?? "",
            ) &&
            architectureFeatureSupport("audioDerivedDuration", capabilities)) ||
        (clip.clipLengthFromControlNet === true &&
            architectureFeatureSupport("icLora", capabilities))
    ) {
        return { status: "not-applicable" };
    }
    const models = effectiveGridModels(clip, modelForName, architectureForId);
    return resolveFrameGridForModelLookup(models, (model) => {
        const entry = modelForName(model);
        return entry?.frameGrid == null || entry.frameGridOrigin == null
            ? null
            : {
                  frameGrid: entry.frameGrid,
                  frameGridOrigin: entry.frameGridOrigin,
              };
    });
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
): FrameGridSpec => {
    const resolution = resolveClipFrameGrid(clip, catalog);
    return resolution.status === "resolved"
        ? {
              frameGrid: resolution.frameGrid,
              frameGridOrigin: resolution.frameGridOrigin,
          }
        : NEUTRAL_FRAME_GRID;
};
