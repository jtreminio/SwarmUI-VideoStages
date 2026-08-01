import { describe, expect, it } from "@jest/globals";
import { minimalClip, minimalRef } from "../__test_helpers__/clipFixtures";
import {
    CONDITIONAL_RULE_CODES,
    evaluateConditionalRule,
} from "./conditionalRules";
import type { CapabilityRuleDecision } from "./types";

const rule = (
    code: (typeof CONDITIONAL_RULE_CODES)[keyof typeof CONDITIONAL_RULE_CODES],
    constraints: Record<string, unknown> | null = null,
): CapabilityRuleDecision => ({
    support: "conditional",
    code,
    reason: code,
    scope: "clip",
    constraints,
});

describe("typed conditional-rule evaluator", () => {
    it("reads the advertised retake entry modes instead of assuming them", () => {
        const generated = minimalClip();
        const requiring = (...modes: string[]) =>
            rule(CONDITIONAL_RULE_CODES.retakeRequiresSource, {
                requiresAnyEntryMode: modes,
            });
        expect(
            evaluateConditionalRule(requiring("text-to-video"), {
                clip: generated,
            }),
        ).toBe(false);
        expect(
            evaluateConditionalRule(requiring("init-video"), {
                clip: generated,
                generatedEntryMode: "image-to-video",
            }),
        ).toBe(true);
        expect(
            evaluateConditionalRule(requiring("init-video", "image-to-video"), {
                clip: generated,
                generatedEntryMode: "image-to-video",
            }),
        ).toBe(false);
    });

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
