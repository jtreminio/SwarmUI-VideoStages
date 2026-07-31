import { describe, expect, it } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
} from "./__test_helpers__/architectureFixtures";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "./__test_helpers__/clipFixtures";
import { deriveAuthoringDiagnostics } from "./authoringDiagnostics";
import { activeStageCount } from "./clipSemantics";
import type { Clip } from "./types";

const codes = (clips: Clip[]): string[] =>
    deriveAuthoringDiagnostics(clips, {
        catalog: testArchitectureCatalog(),
    }).map((item) => item.code);

describe("backend-aligned authoring diagnostics", () => {
    it("counts only active stages for catalog-driven rules", () => {
        const clip = minimalClip({
            stages: [
                minimalStage(),
                minimalStage({ skipped: true }),
                minimalStage(),
            ],
        });
        expect(activeStageCount(clip)).toBe(1);
        clip.stages[1].skipped = false;
        expect(activeStageCount(clip)).toBe(3);
    });

    it("warns when requested audio reuse lacks generate/capture/reuse stages", () => {
        const diagnostics = deriveAuthoringDiagnostics(
            [
                minimalClip({
                    reuseAudio: true,
                    stages: [minimalStage(), minimalStage({ skipped: true })],
                }),
            ],
            { catalog: testArchitectureCatalog() },
        );
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

    it("evaluates the advertised retake source rule", () => {
        const diagnostic = deriveAuthoringDiagnostics(
            [
                minimalClip({
                    retake: {
                        startSeconds: 0,
                        lengthSeconds: 1,
                        strength: 1,
                    },
                }),
            ],
            { catalog: testArchitectureCatalog() },
        ).find(({ code }) => code === "retake-source-required");
        expect(diagnostic?.severity).toBe("error");
    });

    it("allows retake source preconditions in sourced flows", () => {
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

    it("does not diagnose architectures after the first skipped clip", () => {
        const catalog = fakeArchitectureCatalog();
        const fakeClip = minimalClip({
            architectureHint: "test-video",
            modelProfileId: "test-profile",
            stages: [
                minimalStage({
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                }),
            ],
        });
        const invalidTail = minimalClip({
            skipped: true,
            architectureHint: "removed-architecture",
            modelProfileId: "removed-profile",
            stages: [minimalStage({ model: "removed-model.safetensors" })],
        });

        expect(
            deriveAuthoringDiagnostics([fakeClip, invalidTail], {
                catalog,
            }).filter((item) => item.clipIdx === 1),
        ).toEqual([]);
    });

    it("does not apply LTX combination rules to a future architecture that omits them", () => {
        const models = fakeArchitectureCatalog();
        const clip = minimalClip({
            architectureHint: "test-video",
            modelProfileId: "test-profile",
            reuseAudio: true,
            clipLengthFromAudio: true,
            promptWindows: [{ prompt: "relay", start: 0, duration: 1 }],
            refs: [minimalRef()],
            retake: {
                startSeconds: 0,
                lengthSeconds: 1,
                strength: 1,
            },
            stages: [
                minimalStage({
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                }),
            ],
        });

        const diagnostics = deriveAuthoringDiagnostics([clip], {
            catalog: models,
        }).map((item) => item.code);

        expect(diagnostics).not.toEqual(
            expect.arrayContaining([
                "audio.reuse.requires_three_stages",
                "prompt-relay-dynamic-length-unsupported",
                "retake-frame-references-unsupported",
                "retake-source-required",
            ]),
        );
    });
});
