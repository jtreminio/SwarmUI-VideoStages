import {
    defaultAuthoringAudioSource,
    isAllowedAudioSource,
} from "../../audioSource";
import type { Clip } from "../../types";
import { architectureDescriptor, modelCatalogEntry } from "../catalogQueries";
import type { GeneratedArchitectureFeature } from "../generatedFeatures";
import {
    architectureFeatureSupport,
    upscaleModeForMethod,
} from "../policy/featureValues";
import type {
    ArchitectureCapabilities,
    ArchitectureModelCatalog,
    ArchitectureRetargetPlan,
} from "../types";

type ResolvedArchitectureRetarget = ArchitectureRetargetPlan;

const discardUnsupportedFeatures = (
    clip: Clip,
    capabilities: ArchitectureCapabilities,
): void => {
    const supports = (feature: GeneratedArchitectureFeature): boolean =>
        architectureFeatureSupport(feature, capabilities);

    if (!supports("frameReferences")) {
        clip.frameRefs = [];
    }
    if (!supports("promptRelay")) {
        clip.promptWindows = [];
    }
    if (!supports("retake")) {
        clip.retake = null;
    }
    if (!supports("referenceFraming")) {
        clip.refFraming = "crop";
    }
    if (!supports("icLora")) {
        clip.icLoras = [];
        clip.clipLengthFromControlNet = false;
    }
    if (!supports("audioReuse")) {
        clip.reuseAudio = false;
    }
    if (!supports("audioDerivedDuration")) {
        clip.clipLengthFromAudio = false;
    }
    if (!supports("audioBoundaryCarry")) {
        clip.boundaryOutCarryAudio = false;
    }
    if (
        !isAllowedAudioSource(capabilities.audioSourceKinds, clip.audioSource)
    ) {
        clip.audioSource = defaultAuthoringAudioSource(
            capabilities.audioSourceKinds,
        );
        clip.uploadedAudio = null;
        clip.uploadedAudioDurationSeconds = 0;
        clip.uploadedAudioStartSeconds = 0;
        clip.uploadedAudioLengthSeconds = 0;
        clip.saveAudioTrack = false;
    }
    if (!capabilities.entryModes.includes("init-video")) {
        clip.initVideo = null;
    }

    const keepCharacterReferences =
        supports("characterRefineAppearanceReferences") ||
        supports("characterRefineIdentityReferences");
    if (!supports("clipReferences") && !keepCharacterReferences) {
        clip.references = [];
    }

    for (const stage of clip.stages) {
        if (
            !supports("frameReferences") ||
            !supports("stageReferenceStrengths")
        ) {
            stage.frameRefStrengths = [];
        }
        if (!supports("icLora")) {
            stage.icLoraStrengths = [];
        }
        if (!supports("characterRefine")) {
            stage.characterRefine = null;
        } else if (stage.characterRefine) {
            if (!supports("characterRefineAppearanceReferences")) {
                stage.characterRefine.pictureReferenceIds = [];
            }
            if (!supports("characterRefineIdentityReferences")) {
                stage.characterRefine.verifyIdentity = false;
            }
        }
        const upscaleMode = upscaleModeForMethod(stage.upscaleMethod);
        if (
            upscaleMode === "unsupported" ||
            (upscaleMode === "latent" && !supports("latentUpscale")) ||
            (upscaleMode === "latent-model" && !supports("latentModelUpscale"))
        ) {
            stage.upscale = 1;
        }
    }
};

/** Caller-supplied capability data never overrides the catalog. */
export const resolveArchitectureRetarget = (
    requested: Pick<
        ArchitectureRetargetPlan,
        "architectureId" | "modelProfileId" | "model"
    >,
    catalog: ArchitectureModelCatalog | null,
): ResolvedArchitectureRetarget | null => {
    if (!catalog) {
        return null;
    }
    const model = modelCatalogEntry(catalog, requested.model);
    if (
        !model?.architectureId ||
        !model.modelProfileId ||
        model.architectureId !== requested.architectureId
    ) {
        return null;
    }
    const descriptor = architectureDescriptor(catalog, model.architectureId);
    if (!descriptor) {
        return null;
    }
    return {
        architectureId: descriptor.id,
        modelProfileId: model.modelProfileId,
        model: model.value,
    };
};

export const planArchitectureConversion = (
    source: Clip,
    requested: ArchitectureRetargetPlan,
    catalog: ArchitectureModelCatalog | null,
): Clip | null => {
    const target = resolveArchitectureRetarget(requested, catalog);
    if (!target) {
        return null;
    }

    const clip = structuredClone(source);
    clip.architectureHint = target.architectureId;
    clip.modelProfileId = target.modelProfileId;
    for (const stage of clip.stages) {
        stage.model = target.model;
        stage.modelProfileId = target.modelProfileId;
    }
    const capabilities =
        modelCatalogEntry(catalog, target.model)?.capabilities ??
        architectureDescriptor(catalog, target.architectureId)?.capabilities;
    if (!capabilities) {
        return null;
    }
    discardUnsupportedFeatures(clip, capabilities);
    return clip;
};
