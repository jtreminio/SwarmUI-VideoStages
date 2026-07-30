import { isAllowedAudioSource } from "../../audioSource";
import { activeStageCount } from "../../clipSemantics";
import type { Clip, Stage } from "../../types";
import { clipHasActiveHdrForArchitecture } from "../behaviorRegistry";
import {
    CONDITIONAL_RULE_CODES,
    type ConditionalRuleCode,
    conditionalRule,
    evaluateConditionalRule,
} from "../conditionalRules";
import { NONE_ARCHITECTURE_ID } from "../none/identity";
import type {
    ArchitectureCapabilities,
    ArchitectureCatalogEntryDto,
    ArchitectureModelEntry,
    CapabilityRuleDecision,
} from "../types";
import {
    architectureReason,
    noArchitectureReason,
    upscaleModeForMethod,
} from "./featureValues";
import type {
    AuthoringFeature,
    CapabilityDecision,
    CapabilityRuleScopeContext,
    ClipCapabilityView,
    StageCapabilityView,
} from "./types";

type ArchitectureLookup = ReadonlyMap<string, ArchitectureCatalogEntryDto>;
type ModelLookup = ReadonlyMap<string, ArchitectureModelEntry>;

interface EffectiveCatalogIdentity {
    architectureId: string;
    profileId: string;
    descriptor: ArchitectureCatalogEntryDto | undefined;
}

/**
 * Conditional rules that gate an authoring feature. Every consumer reaches
 * these through `decision()`, so a control is disabled where it is authored
 * instead of being enabled and then flagged by the error summary.
 */
const FEATURE_RULE_CODES: Partial<
    Record<AuthoringFeature, readonly ConditionalRuleCode[]>
> = {
    promptRelay: [CONDITIONAL_RULE_CODES.promptRelayRequiresFixedLength],
    audioReuse: [CONDITIONAL_RULE_CODES.audioReuseRequiresStages],
    retake: [
        CONDITIONAL_RULE_CODES.retakeRequiresSource,
        CONDITIONAL_RULE_CODES.retakeExcludesReferences,
    ],
    hdr: [CONDITIONAL_RULE_CODES.uniformTimelineHdr],
};

const conditionalRuleFor = (
    clip: Clip,
    feature: AuthoringFeature,
    descriptor: ArchitectureCatalogEntryDto,
    scope: CapabilityRuleScopeContext,
    effectiveArchitectureId: (target: Clip) => string,
): CapabilityRuleDecision | undefined => {
    const codes = FEATURE_RULE_CODES[feature];
    if (!codes) return undefined;
    for (const code of codes) {
        const rule = conditionalRule(descriptor.rules, code);
        if (
            rule &&
            evaluateConditionalRule(rule, {
                clip,
                globalRefineMode: scope.globalRefineMode,
                timelineClips: scope.timelineClips,
                hasActiveHdr: (target) =>
                    clipHasActiveHdrForArchitecture(
                        target,
                        effectiveArchitectureId(target),
                    ),
            })
        ) {
            return rule;
        }
    }
    return undefined;
};

/** Value-level narrowing for features whose support depends on the authored value. */
export interface FeatureSupportScope {
    capabilities: ArchitectureCapabilities;
    /**
     * Model-profile capability list. `undefined` means "no profile scoping" —
     * the architecture-level answer stands.
     */
    profileCapabilities?: readonly string[];
    /** Persisted audio source, when the caller needs the value checked too. */
    audioSource?: string;
    /** Persisted upscale method, when the caller needs the mode checked too. */
    upscaleMethod?: string;
}

/**
 * The single "does this capability set support feature X" predicate. Capability
 * views, diagnostics, and architecture conversion all answer through it so they
 * cannot disagree about what an architecture supports.
 */
export const architectureFeatureSupport = (
    feature: AuthoringFeature,
    scope: FeatureSupportScope,
): boolean => {
    const capability = scope.capabilities;
    switch (feature) {
        case "multiStage":
            return capability.architecture.includes("multi-stage");
        case "sourceVideo":
            return capability.clip.includes("source-video");
        case "frameReferences":
            return (
                capability.clip.includes("references") &&
                capability.stage.includes("frame-references")
            );
        case "referenceFraming":
            return capability.clip.includes("reference-framing");
        case "retake":
            return capability.clip.includes("retake");
        case "majorPrompt":
            return capability.clip.includes("prompts");
        case "promptRelay":
            return capability.clip.includes("prompt-relay");
        case "clipAudio":
            return (
                capability.clip.includes("audio-sources") &&
                (scope.audioSource === undefined ||
                    isAllowedAudioSource(
                        capability.audioSourceKinds,
                        scope.audioSource,
                    ))
            );
        case "audioReuse":
            return capability.clip.includes("audio-reuse");
        case "audioDerivedDuration":
            return capability.clip.includes("audio-derived-duration");
        case "controlSignalDerivedDuration":
            return capability.clip.includes("control-signal-derived-duration");
        case "stageLoras":
            return (
                capability.stage.includes("lora") &&
                (scope.profileCapabilities === undefined ||
                    scope.profileCapabilities.includes("normal-lora"))
            );
        case "icLora":
            return capability.stage.includes("ic-lora");
        case "hdr":
            return capability.stage.includes("hdr");
        case "upscale":
            return scope.upscaleMethod === undefined
                ? capability.upscaleModes.length > 0
                : capability.upscaleModes.includes(
                      upscaleModeForMethod(scope.upscaleMethod),
                  );
    }
};

const scopedFeatureSupport = (
    feature: AuthoringFeature,
    descriptor: ArchitectureCatalogEntryDto,
    profileId: string,
): boolean =>
    architectureFeatureSupport(feature, {
        capabilities: descriptor.capabilities,
        profileCapabilities: descriptor.profiles.find(
            (entry) => entry.id === profileId,
        )?.capabilities,
    });

export const createClipStageCapabilityViews = (
    architectureById: ArchitectureLookup,
    modelByName: ModelLookup,
    scope: CapabilityRuleScopeContext = {},
): {
    forClip(clip: Clip): ClipCapabilityView;
    forStage(clip: Clip, stage: Stage): StageCapabilityView;
} => {
    const effectiveClipIdentity = (clip: Clip): EffectiveCatalogIdentity => {
        const sourceOnly =
            activeStageCount(clip) === 0 && clip.sourceVideo !== null;
        const resolvedModel = sourceOnly
            ? undefined
            : modelByName.get(clip.stages[0]?.model ?? "");
        const architectureId = sourceOnly
            ? NONE_ARCHITECTURE_ID
            : (resolvedModel?.architectureId ?? clip.architecture);
        const profileId = sourceOnly
            ? NONE_ARCHITECTURE_ID
            : (resolvedModel?.modelProfileId ?? clip.modelProfileId);
        return {
            architectureId,
            profileId,
            descriptor: architectureById.get(architectureId),
        };
    };

    const forClip = (clip: Clip): ClipCapabilityView => {
        const identity = effectiveClipIdentity(clip);
        const { architectureId, profileId, descriptor } = identity;
        const label =
            descriptor?.label ??
            (architectureId === NONE_ARCHITECTURE_ID
                ? "source-only clips"
                : `unknown architecture '${architectureId}'`);
        const decision = (feature: AuthoringFeature): CapabilityDecision => {
            if (!descriptor) {
                return {
                    supported: false,
                    reason: noArchitectureReason(feature),
                    rule: null,
                };
            }
            const conditionalRule = conditionalRuleFor(
                clip,
                feature,
                descriptor,
                scope,
                (target) => effectiveClipIdentity(target).architectureId,
            );
            const supported =
                scopedFeatureSupport(feature, descriptor, profileId) &&
                !conditionalRule;
            return {
                supported,
                reason: supported
                    ? ""
                    : (conditionalRule?.reason ??
                      architectureReason(label, feature)),
                rule: conditionalRule ?? null,
            };
        };
        return {
            architectureId,
            architectureLabel: label,
            known: descriptor !== undefined,
            audioSourceKinds: descriptor?.capabilities.audioSourceKinds ?? [],
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
    };

    const forStage = (clip: Clip, stage: Stage): StageCapabilityView => {
        const view = forClip(clip);
        const sourceOnly =
            view.architectureId === NONE_ARCHITECTURE_ID &&
            activeStageCount(clip) === 0 &&
            clip.sourceVideo !== null;
        const resolvedModel = sourceOnly
            ? undefined
            : modelByName.get(stage.model);
        const architectureId =
            resolvedModel?.architectureId ?? view.architectureId;
        const descriptor = architectureById.get(architectureId);
        const profileId = sourceOnly
            ? NONE_ARCHITECTURE_ID
            : (resolvedModel?.modelProfileId ?? stage.modelProfileId);
        const profile = descriptor?.profiles.find(
            (entry) => entry.id === profileId,
        );
        const decision = (
            feature: "stageLoras" | "upscale" | "sampler" | "scheduler",
        ): CapabilityDecision => {
            if (feature === "stageLoras" && descriptor) {
                const supported =
                    descriptor.capabilities.stage.includes("lora") &&
                    profile?.capabilities.includes("normal-lora") === true;
                const profileRule = supported
                    ? conditionalRule(
                          profile.rules,
                          CONDITIONAL_RULE_CODES.normalLoraRequiresSamplingStage,
                      )
                    : null;
                const violatedRule =
                    profileRule &&
                    evaluateConditionalRule(profileRule, { clip, stage })
                        ? profileRule
                        : null;
                return {
                    supported: supported && !violatedRule,
                    reason:
                        supported && !violatedRule
                            ? ""
                            : (violatedRule?.reason ??
                              `LoRAs require normal-LoRA support in ${descriptor.label}.`),
                    rule: violatedRule,
                };
            }
            if (feature === "sampler" || feature === "scheduler") {
                const required =
                    feature === "sampler"
                        ? "sampler-selection"
                        : "scheduler-selection";
                const supported =
                    profile?.capabilities.includes(required) === true;
                return {
                    supported,
                    reason: supported
                        ? ""
                        : `${feature === "sampler" ? "Sampler" : "Scheduler"} selection is not supported by this model profile.`,
                    rule: null,
                };
            }
            const supported =
                descriptor !== undefined &&
                architectureFeatureSupport("upscale", {
                    capabilities: descriptor.capabilities,
                });
            return {
                supported,
                reason: supported
                    ? ""
                    : descriptor
                      ? architectureReason(descriptor.label, "upscale")
                      : noArchitectureReason("upscale"),
                rule: null,
            };
        };
        return {
            upscaleModes: descriptor?.capabilities.upscaleModes ?? [],
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
    };

    return { forClip, forStage };
};
