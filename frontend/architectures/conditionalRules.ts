import type { Clip, Stage } from "../types";
import type { GeneratedEntryMode } from "./conversion/entryModePolicy";
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
    /**
     * The root's generated mode, needed only to tell a text-to-video clip from an
     * image-to-video one. Init-video clips carry their own mode.
     */
    generatedEntryMode?: GeneratedEntryMode;
}

export type CatalogEntryMode = GeneratedEntryMode | "init-video";

const clipEntryMode = (
    clip: Clip,
    generatedEntryMode: GeneratedEntryMode = "text-to-video",
): CatalogEntryMode =>
    clip.initVideo !== null ? "init-video" : generatedEntryMode;

export const conditionalRule = (
    rules: readonly CapabilityRuleDecision[],
    code: ConditionalRuleCode,
): CapabilityRuleDecision | null =>
    rules.find((rule) => rule.code === code) ?? null;

const DEFAULT_REQUIRED_ENTRY_MODES: readonly CatalogEntryMode[] = [
    "init-video",
];

const requiredEntryModes = (
    rule: CapabilityRuleDecision,
): readonly CatalogEntryMode[] => {
    const value = rule.constraints?.requiresAnyEntryMode;
    return Array.isArray(value) && value.length > 0
        ? (value as CatalogEntryMode[])
        : DEFAULT_REQUIRED_ENTRY_MODES;
};

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
        case CONDITIONAL_RULE_CODES.promptRelayRequiresFixedLength:
            return (
                clip !== undefined &&
                (clip.clipLengthFromAudio || clip.clipLengthFromControlNet)
            );
        case CONDITIONAL_RULE_CODES.retakeExcludesReferences:
            return (
                clip !== undefined &&
                clip.refs.length > 0 &&
                clip.initVideo !== null
            );
        case CONDITIONAL_RULE_CODES.retakeRequiresSource:
            return (
                clip !== undefined &&
                !requiredEntryModes(rule).includes(
                    clipEntryMode(clip, context.generatedEntryMode),
                )
            );
        default:
            // Catalog parsing rejects unknown executable rules atomically.
            // Fail closed as a defense if an unchecked runtime value reaches
            // this evaluator through a test adapter or future integration.
            return true;
    }
};
