import { activeStageCount } from "../../clipSemantics";
import { NEUTRAL_FRAME_GRID } from "../../renderUtils";
import type { Clip, Stage } from "../../types";
import type { GeneratedArchitectureFeature } from "../generatedFeatures";
import { effectiveClipCapabilities } from "../modelCapabilities";
import { NONE_ARCHITECTURE_ID } from "../none/identity";
import {
    clipHasGenerationStageForLookup,
    resolveClipFrameGridForLookup,
} from "../temporalGrid";
import type {
    ArchitectureCatalogEntryDto,
    ArchitectureModelEntry,
} from "../types";
import {
    architectureFeatureSupport,
    architectureReason,
    noArchitectureReason,
    RETAKE_SOURCE_RULE,
    supportsClipAudio,
} from "./featureValues";
import type {
    CapabilityDecision,
    ClipCapabilityView,
    StageCapabilityView,
} from "./types";

type ArchitectureLookup = ReadonlyMap<string, ArchitectureCatalogEntryDto>;
type ModelLookup = ReadonlyMap<string, ArchitectureModelEntry>;

interface EffectiveCatalogIdentity {
    architectureId: string;
    descriptor: ArchitectureCatalogEntryDto | undefined;
}

const UNRESOLVED_ARCHITECTURE_ID = "unsupported";

export const createClipStageCapabilityViews = (
    architectureById: ArchitectureLookup,
    modelByName: ModelLookup,
): {
    forClip(clip: Clip): ClipCapabilityView;
    forStage(clip: Clip, stage: Stage): StageCapabilityView;
} => {
    const clipViews = new WeakMap<Clip, ClipCapabilityView>();
    const stageViews = new WeakMap<Clip, WeakMap<Stage, StageCapabilityView>>();

    const effectiveClipIdentity = (clip: Clip): EffectiveCatalogIdentity => {
        const sourceOnly =
            activeStageCount(clip) === 0 && clip.initVideo !== null;
        const resolvedModel = sourceOnly
            ? undefined
            : modelByName.get(clip.stages[0]?.model ?? "");
        const architectureId = sourceOnly
            ? NONE_ARCHITECTURE_ID
            : (resolvedModel?.architectureId ?? UNRESOLVED_ARCHITECTURE_ID);
        return {
            architectureId,
            descriptor: architectureById.get(architectureId),
        };
    };

    const forClip = (clip: Clip): ClipCapabilityView => {
        const cached = clipViews.get(clip);
        if (cached) {
            return cached;
        }
        const identity = effectiveClipIdentity(clip);
        const { architectureId, descriptor } = identity;
        const capabilities = descriptor
            ? effectiveClipCapabilities(clip, descriptor, (model) =>
                  modelByName.get(model),
              )
            : null;
        const label =
            descriptor?.label ??
            (architectureId === NONE_ARCHITECTURE_ID
                ? "source-only clips"
                : `unknown architecture '${architectureId}'`);
        const decision = (
            feature: GeneratedArchitectureFeature,
        ): CapabilityDecision => {
            if (!descriptor || !capabilities) {
                return {
                    supported: false,
                    reason: noArchitectureReason(feature),
                    code: "",
                };
            }
            const featureSupported = architectureFeatureSupport(
                feature,
                capabilities,
            );
            // Only a clip that could otherwise retake is told it lacks a source.
            const needsRetakeSource =
                feature === "retake" &&
                featureSupported &&
                clip.initVideo === null;
            return {
                supported: featureSupported && !needsRetakeSource,
                reason: needsRetakeSource
                    ? RETAKE_SOURCE_RULE.reason
                    : featureSupported
                      ? ""
                      : architectureReason(label, feature),
                code: needsRetakeSource ? RETAKE_SOURCE_RULE.code : "",
            };
        };
        const frameGridResolution = resolveClipFrameGridForLookup(
            clip,
            (model) => modelByName.get(model),
            (architectureId) => architectureById.get(architectureId),
        );
        const hasGenerationStage = clipHasGenerationStageForLookup(
            clip,
            (model) => modelByName.get(model),
            (architectureId) => architectureById.get(architectureId),
        );
        const view: ClipCapabilityView = {
            architectureId,
            architectureLabel: label,
            known: descriptor !== undefined,
            frameGrid:
                frameGridResolution.status === "resolved"
                    ? {
                          frameGrid: frameGridResolution.frameGrid,
                          frameGridOrigin: frameGridResolution.frameGridOrigin,
                      }
                    : NEUTRAL_FRAME_GRID,
            frameGridResolution,
            hasGenerationStage,
            audioSourceKinds: capabilities?.audioSourceKinds ?? [],
            clipAudio: {
                supported: supportsClipAudio(
                    capabilities?.audioSourceKinds ?? [],
                ),
                reason: supportsClipAudio(capabilities?.audioSourceKinds ?? [])
                    ? ""
                    : `Clip audio is not supported by ${label}.`,
                code: "",
            },
            decision,
            authoringState: (feature, persisted) => {
                const result = decision(feature);
                return {
                    ...result,
                    visible: result.supported || persisted,
                    enabled: result.supported,
                };
            },
        };
        clipViews.set(clip, view);
        return view;
    };

    const forStage = (clip: Clip, stage: Stage): StageCapabilityView => {
        let viewsForClip = stageViews.get(clip);
        if (!viewsForClip) {
            viewsForClip = new WeakMap<Stage, StageCapabilityView>();
            stageViews.set(clip, viewsForClip);
        }
        const cached = viewsForClip.get(stage);
        if (cached) {
            return cached;
        }
        const view = forClip(clip);
        const sourceOnly =
            view.architectureId === NONE_ARCHITECTURE_ID &&
            activeStageCount(clip) === 0 &&
            clip.initVideo !== null;
        const resolvedModel = sourceOnly
            ? undefined
            : modelByName.get(stage.model);
        const architectureId = sourceOnly
            ? NONE_ARCHITECTURE_ID
            : (resolvedModel?.architectureId ?? UNRESOLVED_ARCHITECTURE_ID);
        const descriptor = architectureById.get(architectureId);
        const decision = (
            feature: "sampler" | "scheduler",
        ): CapabilityDecision => {
            const supported =
                descriptor !== undefined &&
                resolvedModel !== undefined &&
                resolvedModel.entryModes.length > 0;
            return {
                supported,
                reason: supported
                    ? ""
                    : `${feature === "sampler" ? "Sampler" : "Scheduler"} selection requires a resolved generating video model.`,
                code: "",
            };
        };
        const stageView: StageCapabilityView = {
            decision,
            authoringState: (feature, persisted) => {
                const result = decision(feature);
                return {
                    ...result,
                    visible: result.supported || persisted,
                    enabled: result.supported,
                };
            },
        };
        viewsForClip.set(stage, stageView);
        return stageView;
    };

    return { forClip, forStage };
};
