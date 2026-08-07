import { describe, expect, it } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
} from "./__test_helpers__/architectureFixtures";
import {
    icLoraFixture,
    minimalClip,
    minimalRef,
    minimalStage,
} from "./__test_helpers__/clipFixtures";
import { createCapabilityViewResolver } from "./architectures/policy";
import { deriveAuthoringDiagnostics } from "./authoringDiagnostics";
import { activeStageCount } from "./clipSemantics";
import type { Clip } from "./types";

const codes = (clips: Clip[]): string[] =>
    deriveAuthoringDiagnostics(
        clips,
        createCapabilityViewResolver(testArchitectureCatalog()),
    ).map((item) => item.code);

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

    it("accepts audio reuse an architecture supports", () => {
        expect(codes([minimalClip({ reuseAudio: true })])).toEqual([]);
    });

    it.each<Partial<Clip>>([
        { clipLengthFromAudio: true, audioSource: "Upload" },
        {
            clipLengthFromControlNet: true,
            icLoras: [icLoraFixture({ driveSource: "ControlNet 3" })],
        },
    ])("accepts prompt relay with a dynamic duration owner", (lengthOwner) => {
        expect(
            codes([
                minimalClip({
                    ...lengthOwner,
                    promptWindows: [{ prompt: "move", start: 0, duration: 1 }],
                }),
            ]),
        ).toEqual([]);
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
            createCapabilityViewResolver(testArchitectureCatalog()),
        ).find(({ code }) => code === "retake-source-required");
        expect(diagnostic?.severity).toBe("error");
    });

    it("allows retake source preconditions in init-video flows", () => {
        const retake = {
            startSeconds: 0,
            lengthSeconds: 1,
            strength: 1,
        };
        const initVideoClip = minimalClip({
            retake,
            initVideo: {
                data: "data:video/mp4;base64,QQ==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
        });
        expect(codes([initVideoClip])).not.toContain("retake-source-required");
    });

    it("accepts frame references combined with an executable retake", () => {
        expect(
            codes([
                minimalClip({
                    initVideo: {
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
                    frameRefs: [minimalRef()],
                }),
            ]),
        ).toEqual([]);
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
            deriveAuthoringDiagnostics(
                [fakeClip, invalidTail],
                createCapabilityViewResolver(catalog),
            ).filter((item) => item.clipIdx === 1),
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
            frameRefs: [minimalRef()],
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

        const diagnostics = deriveAuthoringDiagnostics(
            [clip],
            createCapabilityViewResolver(models),
        ).map((item) => item.code);

        expect(diagnostics).not.toEqual(
            expect.arrayContaining(["retake-source-required"]),
        );
    });
});
