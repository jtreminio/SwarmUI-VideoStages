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

/**
 * Evaluates the architecture-owned condition independently of whether its
 * feature is authored. Capability views use this for availability while
 * diagnostics additionally require the affected persisted feature.
 */
export const evaluateConditionalRule = (
    rule: CapabilityRuleDecision,
    context: ConditionalRuleContext,
): boolean => {
    switch (rule.code as ConditionalRuleCode) {
        case CONDITIONAL_RULE_CODES.retakeRequiresSource:
            return (
                context.clip !== undefined && context.clip.initVideo === null
            );
        default:
            // Catalog parsing rejects unknown executable rules atomically.
            // Fail closed as a defense if an unchecked runtime value reaches
            // this evaluator through a test adapter or future integration.
            return true;
    }
};
