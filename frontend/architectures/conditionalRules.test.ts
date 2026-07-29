import { describe, expect, it } from "@jest/globals";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import {
    audioReuseMinimumActiveStages,
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
    entityId: null,
    constraints,
});

describe("typed conditional-rule evaluator", () => {
    it("reads the advertised audio-reuse stage minimum", () => {
        const reuseRule = rule(
            CONDITIONAL_RULE_CODES.audioReuseRequiresStages,
            { minimumActiveStages: 4 },
        );
        expect(audioReuseMinimumActiveStages(reuseRule)).toBe(4);
        expect(
            evaluateConditionalRule(reuseRule, {
                clip: minimalClip({
                    stages: [minimalStage(), minimalStage(), minimalStage()],
                }),
            }),
        ).toBe(true);
    });

    it("shares stage and dynamic-length conditions with capability views", () => {
        const clip = minimalClip({
            clipLengthFromAudio: true,
            stages: [minimalStage(), minimalStage()],
        });
        expect(
            evaluateConditionalRule(
                rule(CONDITIONAL_RULE_CODES.audioReuseRequiresStages, {
                    minimumActiveStages: 3,
                }),
                { clip },
            ),
        ).toBe(true);
        expect(
            evaluateConditionalRule(
                rule(CONDITIONAL_RULE_CODES.promptRelayRequiresFixedLength),
                { clip },
            ),
        ).toBe(true);
    });

    it("reads the advertised exclusive stage-control minimum", () => {
        const samplingRule = {
            ...rule(CONDITIONAL_RULE_CODES.normalLoraRequiresSamplingStage, {
                exclusiveMinimumControl: 0,
            }),
            scope: "stage" as const,
        };
        expect(
            evaluateConditionalRule(samplingRule, {
                stage: minimalStage({ control: 0 }),
            }),
        ).toBe(true);
        expect(
            evaluateConditionalRule(samplingRule, {
                stage: minimalStage({ control: -0.1 }),
            }),
        ).toBe(true);
        expect(
            evaluateConditionalRule(samplingRule, {
                stage: minimalStage({ control: 0.01 }),
            }),
        ).toBe(false);
    });

    it("evaluates retake entry and reference conditions", () => {
        const generated = minimalClip();
        expect(
            evaluateConditionalRule(
                rule(CONDITIONAL_RULE_CODES.retakeRequiresSource),
                { clip: generated },
            ),
        ).toBe(true);

        const sourced = minimalClip({
            refs: [minimalRef({ source: "Upload", frame: 1, fromEnd: false })],
            sourceVideo: {
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
                rule(CONDITIONAL_RULE_CODES.retakeExcludesReferences),
                { clip: sourced },
            ),
        ).toBe(true);
    });

    it("evaluates timeline HDR uniformity with its advertised minimum", () => {
        const hdr = minimalClip({ prompt: "hdr" });
        const plain = minimalClip();
        expect(
            evaluateConditionalRule(
                rule(CONDITIONAL_RULE_CODES.uniformTimelineHdr, {
                    minimumTimelineClips: 2,
                }),
                {
                    timelineClips: [hdr, plain],
                    hasActiveHdr: (clip) => clip.prompt === "hdr",
                },
            ),
        ).toBe(true);
    });
});
