import { describe, expect, it } from "@jest/globals";
import { minimalClip, minimalRef } from "../__test_helpers__/clipFixtures";
import {
    CONDITIONAL_RULE_CODES,
    evaluateConditionalRule,
} from "./conditionalRules";
import type { CapabilityRuleDecision } from "./types";

const rule = (
    code: (typeof CONDITIONAL_RULE_CODES)[keyof typeof CONDITIONAL_RULE_CODES],
): CapabilityRuleDecision => ({
    support: "conditional",
    code,
    reason: code,
    scope: "clip",
    constraints: null,
});

describe("typed conditional-rule evaluator", () => {
    it("requires an init video for retake, references notwithstanding", () => {
        expect(
            evaluateConditionalRule(
                rule(CONDITIONAL_RULE_CODES.retakeRequiresSource),
                { clip: minimalClip() },
            ),
        ).toBe(true);

        const initVideoClip = minimalClip({
            refs: [minimalRef({ source: "Upload", frame: 1, fromEnd: false })],
            initVideo: {
                data: "data:video/mp4;base64,AA==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
        });
        expect(
            evaluateConditionalRule(
                rule(CONDITIONAL_RULE_CODES.retakeRequiresSource),
                { clip: initVideoClip },
            ),
        ).toBe(false);
    });
});
