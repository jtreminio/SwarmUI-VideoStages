import { activeStageCount } from "../clipSemantics";
import type { Clip, Stage } from "../types";
import {
    CONDITIONAL_RULE_CODES,
    type GeneratedConditionalRuleCode,
} from "./generatedFeatures";
import type { CapabilityRuleDecision } from "./types";

export { CONDITIONAL_RULE_CODES };

export type ConditionalRuleCode = GeneratedConditionalRuleCode;

const KNOWN_CONDITIONAL_RULE_CODES = new Set<string>(
    Object.values(CONDITIONAL_RULE_CODES),
);

export const isKnownConditionalRuleCode = (
    value: string,
): value is ConditionalRuleCode => KNOWN_CONDITIONAL_RULE_CODES.has(value);

export interface ConditionalRuleContext {
    clip?: Clip;
    stage?: Stage;
}

export const conditionalRule = (
    rules: readonly CapabilityRuleDecision[],
    code: ConditionalRuleCode,
): CapabilityRuleDecision | null =>
    rules.find((rule) => rule.code === code) ?? null;

const finiteConstraint = (
    rule: CapabilityRuleDecision,
    key: string,
    fallback: number,
): number => {
    const value = Number(rule.constraints?.[key]);
    return Number.isFinite(value) ? value : fallback;
};

const DEFAULT_AUDIO_REUSE_MINIMUM_ACTIVE_STAGES = 3;

export const audioReuseMinimumActiveStages = (
    rule: CapabilityRuleDecision | null | undefined,
): number =>
    rule?.code === CONDITIONAL_RULE_CODES.audioReuseRequiresStages
        ? finiteConstraint(
              rule,
              "minimumActiveStages",
              DEFAULT_AUDIO_REUSE_MINIMUM_ACTIVE_STAGES,
          )
        : DEFAULT_AUDIO_REUSE_MINIMUM_ACTIVE_STAGES;

/**
 * Evaluates the architecture-owned condition independently of whether its
 * feature is authored. Capability views use this for availability while
 * diagnostics additionally require the affected persisted feature.
 */
export const evaluateConditionalRule = (
    rule: CapabilityRuleDecision,
    context: ConditionalRuleContext,
): boolean => {
    const clip = context.clip;
    switch (rule.code as ConditionalRuleCode) {
        case CONDITIONAL_RULE_CODES.audioReuseRequiresStages:
            return (
                clip !== undefined &&
                activeStageCount(clip) < audioReuseMinimumActiveStages(rule)
            );
        case CONDITIONAL_RULE_CODES.normalLoraRequiresSamplingStage:
            return (
                context.stage !== undefined &&
                context.stage.control <=
                    finiteConstraint(rule, "exclusiveMinimumControl", 0)
            );
        case CONDITIONAL_RULE_CODES.promptRelayRequiresFixedLength:
            return (
                clip !== undefined &&
                (clip.clipLengthFromAudio || clip.clipLengthFromControlNet)
            );
        case CONDITIONAL_RULE_CODES.retakeExcludesReferences:
            return (
                clip !== undefined &&
                clip.refs.length > 0 &&
                clip.sourceVideo !== null
            );
        case CONDITIONAL_RULE_CODES.retakeRequiresSource:
            return clip !== undefined && clip.sourceVideo === null;
        default:
            // Catalog parsing rejects unknown executable rules atomically.
            // Fail closed as a defense if an unchecked runtime value reaches
            // this evaluator through a test adapter or future integration.
            return true;
    }
};
