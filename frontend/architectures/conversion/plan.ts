import {
    canUseClipLengthFromAudio,
    defaultAuthoringAudioSource,
} from "../../audioSource";
import {
    IC_LORA_SOURCE_UPLOAD,
    IC_LORA_STAGE_ALL,
} from "../../icLoraAuthoring";
import { removeIcLoraStrengthAt } from "../../normalizationStage";
import type { Clip, IcLora } from "../../types";
import { isArchitectureHdrFeature } from "../behaviorRegistry";
import { architectureDescriptor, modelCatalogEntry } from "../catalogQueries";
import { modelIdentityFromCatalog } from "../clipIdentity";
import {
    CONDITIONAL_RULE_CODES,
    conditionalRule,
    evaluateConditionalRule,
} from "../conditionalRules";
import { NONE_ARCHITECTURE_ID } from "../none/identity";
import { architectureFeatureSupport } from "../policy/clipStageViews";
import type { AuthoringFeature } from "../policy/types";
import type {
    ArchitectureModelCatalog,
    ArchitectureRetargetPlan,
    CapabilityRuleDecision,
} from "../types";

const countLabel = (count: number, singular: string): string =>
    `${count} ${singular}${count === 1 ? "" : "s"}`;

export interface ArchitectureConversionPlan {
    /** The complete converted clip; the source clip is never mutated. */
    clip: Clip;
    /** User-facing summary produced from the same decisions as `clip`. */
    removals: string[];
    /** Stable IDs removed by the conversion, for selection invalidation. */
    removedEntityIds: string[];
    selectionAffected: boolean;
}

interface ResolvedArchitectureRetarget extends ArchitectureRetargetPlan {
    profileCapabilities: string[];
    profileRules: CapabilityRuleDecision[];
}

const ownId = (value: unknown): string | null =>
    typeof value === "object" &&
    value !== null &&
    "id" in value &&
    typeof value.id === "string"
        ? value.id
        : null;

const collectIds = (values: readonly unknown[]): string[] =>
    values.map(ownId).filter((id): id is string => id !== null);

/**
 * Resolves a caller-supplied target against the catalog. Every supplied
 * identity must match; caller-supplied capability arrays never self-authorize.
 */
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
        model.architectureId !== requested.architectureId ||
        model.modelProfileId !== requested.modelProfileId
    ) {
        return null;
    }
    const descriptor = architectureDescriptor(catalog, model.architectureId);
    const profile = descriptor?.profiles.find(
        (entry) => entry.id === model.modelProfileId,
    );
    if (!descriptor || !profile) {
        return null;
    }
    return {
        architectureId: descriptor.id,
        modelProfileId: profile.id,
        model: model.value,
        capabilities: structuredClone(descriptor.capabilities),
        entryModes: [...profile.entryModes],
        profileCapabilities: [...profile.capabilities],
        profileRules: structuredClone(profile.rules),
    };
};

/**
 * Plans and applies one architecture conversion on a private clone.
 *
 * Preview and reducer both consume this function, so the confirmation summary
 * cannot drift from the actual atomic mutation.
 */
export const planArchitectureConversion = (
    source: Clip,
    requested: ArchitectureRetargetPlan,
    catalog: ArchitectureModelCatalog | null,
): ArchitectureConversionPlan | null => {
    const target = resolveArchitectureRetarget(requested, catalog);
    if (!target) {
        return null;
    }

    const clip = structuredClone(source);
    const removals: string[] = [];
    const removedEntityIds: string[] = [];
    const supports = (
        feature: AuthoringFeature,
        value?: { audioSource?: string; upscaleMethod?: string },
    ): boolean =>
        architectureFeatureSupport(feature, {
            capabilities: target.capabilities,
            profileCapabilities: target.profileCapabilities,
            ...value,
        });
    const supportsMultipleStages = supports("multiStage");
    const supportsReferences = supports("frameReferences");
    const supportsNormalLoras = supports("stageLoras");
    const samplingStageRule = conditionalRule(
        target.profileRules,
        CONDITIONAL_RULE_CODES.normalLoraRequiresSamplingStage,
    );

    clip.architecture = target.architectureId;
    clip.modelProfileId = target.modelProfileId;

    // The envelope belongs to the architecture that wrote it, so it cannot ride
    // along to a different adapter, which would read foreign schema as its own.
    // Undo does not need it kept here: history holds the pre-conversion document.
    // `none` owns no envelope, so a dormant sourced clip is owned by its authored
    // Stage 0 model alone — asking for a whole-clip identity would read repairable
    // stage state as "no owner" and drop the payload of an unchanged owner.
    // Nothing authors an envelope yet, so a whole-document save that changes owner
    // while carrying one is refused rather than reinterpreted; when an adapter
    // starts writing payloads, that save path needs its own replacement rule.
    const payloadOwner =
        source.architecture === NONE_ARCHITECTURE_ID
            ? (modelIdentityFromCatalog(catalog, source.stages[0]?.model ?? "")
                  ?.architectureId ?? source.architecture)
            : source.architecture;
    if (
        payloadOwner !== target.architectureId &&
        clip.architecturePayload !== null
    ) {
        removals.push("architecture-specific payload");
        clip.architecturePayload = null;
    }

    if (!supportsMultipleStages && clip.stages.length > 1) {
        const removedStages = clip.stages.slice(1);
        removals.push(countLabel(removedStages.length, "later authored stage"));
        removedEntityIds.push(...collectIds(removedStages));
        clip.stages = clip.stages.slice(0, 1);
    }

    // Each feature owns its COMPLETE state: references own refs + refStrengths,
    // IC-LoRA owns icLoras + icLoraStrengths (below). Neither clears the other's
    // per-stage strengths.
    if (!supportsReferences && clip.refs.length > 0) {
        removals.push(countLabel(clip.refs.length, "frame reference"));
        removedEntityIds.push(...collectIds(clip.refs));
        clip.refs = [];
    }
    if (!supports("referenceFraming") && clip.refFraming !== "crop") {
        removals.push("reference framing setting");
        clip.refFraming = "crop";
    }

    const removedClipLoras = !supportsNormalLoras ? clip.loras.length : 0;
    if (removedClipLoras > 0) {
        clip.loras = [];
    }
    let removedUpscaleSettings = 0;
    let repairedNormalLoraWeights = 0;
    for (const stage of clip.stages) {
        stage.model = target.model;
        stage.modelProfileId = target.modelProfileId;
        if (!supportsReferences) {
            stage.refStrengths = [];
        }
        if (!supportsNormalLoras) {
            stage.loraWeights = [];
        } else if (
            samplingStageRule &&
            evaluateConditionalRule(samplingStageRule, { clip, stage })
        ) {
            for (let index = 0; index < clip.loras.length; index++) {
                if ((stage.loraWeights[index] ?? 1) !== 0) {
                    stage.loraWeights[index] = 0;
                    repairedNormalLoraWeights++;
                }
            }
        }
        if (
            stage.upscale !== 1 &&
            !supports("upscale", { upscaleMethod: stage.upscaleMethod })
        ) {
            removedUpscaleSettings++;
            stage.upscale = 1;
        }
    }
    if (removedClipLoras > 0) {
        removals.push(countLabel(removedClipLoras, "clip LoRA"));
    }
    if (repairedNormalLoraWeights > 0) {
        removals.push(
            `${repairedNormalLoraWeights} normal LoRA ${
                repairedNormalLoraWeights === 1 ? "weight" : "weights"
            } disabled on samplerless stages`,
        );
    }
    if (removedUpscaleSettings > 0) {
        removals.push("stage upscale settings");
    }

    /** Removes IC-LoRA entries with the per-stage strengths and ids they own. */
    const removeIcLoras = (doomed: (entry: IcLora) => boolean): number => {
        const removed = clip.icLoras.filter(doomed);
        if (removed.length === 0) {
            return 0;
        }
        for (let index = clip.icLoras.length - 1; index >= 0; index--) {
            if (doomed(clip.icLoras[index])) {
                removeIcLoraStrengthAt(clip, index);
            }
        }
        removedEntityIds.push(...collectIds(removed));
        clip.icLoras = clip.icLoras.filter((entry) => !doomed(entry));
        return removed.length;
    };

    if (!supports("icLora") && clip.icLoras.length > 0) {
        removals.push(
            countLabel(
                removeIcLoras(() => true),
                "IC-LoRA",
            ),
        );
    } else if (supports("icLora")) {
        if (!supports("hdr")) {
            const hdrCount = removeIcLoras((entry) =>
                isArchitectureHdrFeature(source.architecture, entry),
            );
            if (hdrCount > 0) {
                removals.push(countLabel(hdrCount, "HDR IC-LoRA"));
            }
        }
        let repairedTargets = false;
        for (const entry of clip.icLoras) {
            if (entry.stage >= clip.stages.length) {
                entry.stage = IC_LORA_STAGE_ALL;
                repairedTargets = true;
            }
            if (
                entry.driveData === "none" &&
                entry.driveSource !== IC_LORA_SOURCE_UPLOAD
            ) {
                entry.driveSource = IC_LORA_SOURCE_UPLOAD;
                entry.driveMedia = null;
            }
        }
        if (repairedTargets) {
            removals.push("IC-LoRA targets on removed stages");
        }
    }

    if (!supports("retake") && clip.retake !== null) {
        removals.push("retake");
        const id = ownId(clip.retake);
        if (id) removedEntityIds.push(id);
        clip.retake = null;
    }
    if (!supports("majorPrompt") && clip.prompt.trim()) {
        removals.push("major prompt");
        clip.prompt = "";
    }
    if (!supports("promptRelay") && clip.promptWindows.length > 0) {
        removals.push(countLabel(clip.promptWindows.length, "relay prompt"));
        removedEntityIds.push(...collectIds(clip.promptWindows));
        clip.promptWindows = [];
    }
    if (!supports("sourceVideo") && clip.sourceVideo) {
        removals.push("source video");
        clip.sourceVideo = null;
    }
    if (!supports("audioReuse") && clip.reuseAudio) {
        removals.push("captured stage audio reuse");
        clip.reuseAudio = false;
    }
    if (
        !supports("controlSignalDerivedDuration") &&
        clip.clipLengthFromControlNet
    ) {
        removals.push("control-signal-derived clip duration");
        clip.clipLengthFromControlNet = false;
    }
    if (!supports("clipAudio", { audioSource: clip.audioSource })) {
        const hasAudioSettings =
            clip.audioSource !== "Native" ||
            clip.uploadedAudio !== null ||
            clip.saveAudioTrack;
        if (hasAudioSettings) {
            removals.push("clip audio source settings");
        }
        clip.audioSource = defaultAuthoringAudioSource(
            target.capabilities.audioSourceKinds,
        );
        clip.uploadedAudio = null;
        clip.saveAudioTrack = false;
    }
    if (
        clip.clipLengthFromAudio &&
        (!supports("audioDerivedDuration") ||
            !canUseClipLengthFromAudio(clip.audioSource))
    ) {
        removals.push("audio-derived clip duration");
        clip.clipLengthFromAudio = false;
    }

    return {
        clip,
        removals,
        removedEntityIds,
        selectionAffected: removedEntityIds.length > 0,
    };
};
