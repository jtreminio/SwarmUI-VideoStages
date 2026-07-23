import { describe, expect, it } from "@jest/globals";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "./__test_helpers__/clipFixtures";
import {
    activeStageCount,
    deriveAuthoringDiagnostics,
    isAudioReuseEligible,
} from "./authoringDiagnostics";
import type { Clip } from "./types";

const codes = (clips: Clip[]): string[] =>
    deriveAuthoringDiagnostics(clips).map((item) => item.code);

describe("backend-aligned authoring diagnostics", () => {
    it("counts active stages and requires three for captured-stage audio reuse", () => {
        const clip = minimalClip({
            stages: [
                minimalStage(),
                minimalStage({ skipped: true }),
                minimalStage(),
            ],
        });
        expect(activeStageCount(clip)).toBe(2);
        expect(isAudioReuseEligible(clip)).toBe(false);
        clip.stages[1].skipped = false;
        expect(isAudioReuseEligible(clip)).toBe(true);
    });

    it("warns when requested audio reuse lacks generate/capture/reuse stages", () => {
        const diagnostics = deriveAuthoringDiagnostics([
            minimalClip({
                reuseAudio: true,
                stages: [minimalStage(), minimalStage({ skipped: true })],
            }),
        ]);
        expect(diagnostics).toContainEqual({
            severity: "warning",
            code: "audio.reuse.requires_three_stages",
            message:
                "Audio reuse needs at least three active stages: generate, capture, then reuse.",
            clipIdx: 0,
        });
    });

    it.each([
        { clipLengthFromAudio: true, clipLengthFromControlNet: false },
        { clipLengthFromAudio: false, clipLengthFromControlNet: true },
    ])("rejects prompt relay with a dynamic duration owner", (lengthFlags) => {
        expect(
            codes([
                minimalClip({
                    ...lengthFlags,
                    promptWindows: [{ prompt: "move", start: 0, duration: 1 }],
                }),
            ]),
        ).toContain("prompt-relay-dynamic-length-unsupported");
    });

    it("warns that an ordinary unsourced retake will not execute", () => {
        expect(
            codes([
                minimalClip({
                    retake: {
                        startSeconds: 0,
                        lengthSeconds: 1,
                        strength: 1,
                    },
                }),
            ]),
        ).toContain("retake-source-required");
    });

    it("allows retake source preconditions in sourced or global-refine flows", () => {
        const retake = {
            startSeconds: 0,
            lengthSeconds: 1,
            strength: 1,
        };
        const sourced = minimalClip({
            retake,
            sourceVideo: {
                data: "data:video/mp4;base64,QQ==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
        });
        expect(codes([sourced])).not.toContain("retake-source-required");
        expect(
            deriveAuthoringDiagnostics([minimalClip({ retake })], {
                globalRefineMode: true,
            }).map((item) => item.code),
        ).not.toContain("retake-source-required");
    });

    it("rejects frame references combined with an executable retake", () => {
        expect(
            codes([
                minimalClip({
                    sourceVideo: {
                        data: "data:video/mp4;base64,QQ==",
                        fileName: "source.mp4",
                        fps: 24,
                        durationSeconds: 2,
                        startSeconds: 0,
                        lengthSeconds: 2,
                    },
                    retake: {
                        startSeconds: 0,
                        lengthSeconds: 1,
                        strength: 1,
                    },
                    refs: [minimalRef()],
                }),
            ]),
        ).toContain("retake-frame-references-unsupported");
    });

    it("rejects a mixed HDR and non-HDR executable timeline", () => {
        const hdr = minimalClip({
            icLoras: [
                {
                    lora: "ltx-ic-lora-hdr.safetensors",
                    preset: "hdr",
                    source: "Upload",
                    stage: -1,
                    strength: 1,
                    attentionStrength: 1,
                    controlType: "none",
                    video: null,
                    driveAudioRef: false,
                },
            ],
        });
        expect(codes([hdr, minimalClip()])).toContain(
            "mixed-hdr-timeline-unsupported",
        );
        expect(codes([hdr, structuredClone(hdr)])).not.toContain(
            "mixed-hdr-timeline-unsupported",
        );
    });

    it("ignores skipped clips and HDR entries targeting skipped stages", () => {
        const inactiveHdr = minimalClip({
            icLoras: [
                {
                    lora: "ltx-ic-lora-hdr.safetensors",
                    preset: "hdr",
                    source: "Upload",
                    stage: 1,
                    strength: 1,
                    attentionStrength: 1,
                    controlType: "none",
                    video: null,
                    driveAudioRef: false,
                },
            ],
            stages: [minimalStage(), minimalStage({ skipped: true })],
        });
        expect(codes([inactiveHdr, minimalClip()])).not.toContain(
            "mixed-hdr-timeline-unsupported",
        );
        expect(
            codes([
                minimalClip({ ...structuredClone(inactiveHdr), skipped: true }),
                minimalClip(),
            ]),
        ).not.toContain("mixed-hdr-timeline-unsupported");
    });
});
