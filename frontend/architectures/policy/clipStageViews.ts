import { activeStageCount } from "../../clipSemantics";
import type { Clip, Stage } from "../../types";
import { clipHasActiveHdrForArchitecture } from "../behaviorRegistry";
import {
    CONDITIONAL_RULE_CODES,
    type ConditionalRuleCode,
    conditionalRule,
    evaluateConditionalRule,
} from "../conditionalRules";
import {
    effectiveClipCapabilities,
    effectiveModelCapabilities,
} from "../modelCapabilities";
import { NONE_ARCHITECTURE_ID } from "../none/identity";
import { resolveClipFrameGridForLookup } from "../temporalGrid";
import type {
    ArchitectureCatalogEntryDto,
    ArchitectureModelEntry,
    CapabilityRuleDecision,
} from "../types";
import {
    architectureFeatureSupport,
    architectureReason,
    noArchitectureReason,
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
    descriptor: ArchitectureCatalogEntryDto | undefined;
}

const UNRESOLVED_ARCHITECTURE_ID = "unsupported";

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
            : (resolvedModel?.architectureId ?? UNRESOLVED_ARCHITECTURE_ID);
        return {
            architectureId,
            descriptor: architectureById.get(architectureId),
        };
    };

    const forClip = (clip: Clip): ClipCapabilityView => {
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
        const decision = (feature: AuthoringFeature): CapabilityDecision => {
            if (!descriptor || !capabilities) {
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
                architectureFeatureSupport(feature, {
                    capabilities,
                }) && !conditionalRule;
            return {
                supported,
                reason: supported
                    ? ""
                    : (conditionalRule?.reason ??
                      architectureReason(label, feature)),
                rule: conditionalRule ?? null,
            };
        };
        const frameGridResolution = resolveClipFrameGridForLookup(
            clip,
            (model) => modelByName.get(model),
            (architectureId) => architectureById.get(architectureId),
            { globalRefineMode: scope.globalRefineMode },
        );
        return {
            architectureId,
            architectureLabel: label,
            known: descriptor !== undefined,
            frameGrid:
                frameGridResolution.status === "resolved"
                    ? frameGridResolution.frameGrid
                    : 1,
            frameGridResolution,
            audioSourceKinds: capabilities?.audioSourceKinds ?? [],
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
        const architectureId = sourceOnly
            ? NONE_ARCHITECTURE_ID
            : (resolvedModel?.architectureId ?? UNRESOLVED_ARCHITECTURE_ID);
        const descriptor = architectureById.get(architectureId);
        const capabilities = descriptor
            ? effectiveModelCapabilities(resolvedModel, descriptor)
            : null;
        const decision = (
            feature: "stageLoras" | "upscale" | "sampler" | "scheduler",
        ): CapabilityDecision => {
            if (feature === "stageLoras" && descriptor && capabilities) {
                const supported = architectureFeatureSupport(feature, {
                    capabilities,
                });
                const architectureRule = supported
                    ? conditionalRule(
                          descriptor.rules,
                          CONDITIONAL_RULE_CODES.normalLoraRequiresSamplingStage,
                      )
                    : null;
                const violatedRule =
                    architectureRule &&
                    evaluateConditionalRule(architectureRule, { clip, stage })
                        ? architectureRule
                        : null;
                return {
                    supported: supported && !violatedRule,
                    reason:
                        supported && !violatedRule
                            ? ""
                            : (violatedRule?.reason ??
                              `LoRAs are not supported by ${descriptor.label}.`),
                    rule: violatedRule,
                };
            }
            if (feature === "sampler" || feature === "scheduler") {
                const supported =
                    descriptor !== undefined &&
                    resolvedModel !== undefined &&
                    ((resolvedModel.entryAbilities?.length ?? 0) > 0 ||
                        resolvedModel.entryModes.length > 0);
                return {
                    supported,
                    reason: supported
                        ? ""
                        : `${feature === "sampler" ? "Sampler" : "Scheduler"} selection requires a resolved generating video model.`,
                    rule: null,
                };
            }
            const supported =
                descriptor !== undefined &&
                capabilities !== null &&
                architectureFeatureSupport("upscale", {
                    capabilities,
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
            upscaleModes: capabilities?.upscaleModes ?? [],
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
