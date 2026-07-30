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
import {
    activeStageCount,
    deriveAuthoringDiagnostics,
} from "./authoringDiagnostics";
import type { Clip } from "./types";

const catalogWithWan = () => {
    const catalog = testArchitectureCatalog();
    const ltx = catalog.architectures[0];
    const wan = structuredClone(ltx);
    wan.id = "wan22";
    wan.label = "WAN";
    wan.defaultProfileId = "wan-profile";
    wan.rules = [];
    wan.profiles = [
        {
            ...wan.profiles[0],
            id: "wan-profile",
            label: "WAN",
        },
    ];
    catalog.architectures.push(wan);
    catalog.entries.push({
        value: "wan.safetensors",
        label: "WAN",
        architectureId: "wan22",
        modelProfileId: "wan-profile",
    });
    return catalog;
};

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
                catalog: testArchitectureCatalog(),
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
                    driveSource: "Upload",
                    driveData: "visual",
                    driveMediaKinds: ["image", "video"],
                    stage: -1,
                    strength: 1,
                    attentionStrength: 1,
                    controlType: "none",
                    hdr: true,
                    driveMedia: null,
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

    it("does not apply stale LTX HDR ownership to a model that resolves as WAN", () => {
        const staleWan = minimalClip({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
            icLoras: [
                {
                    lora: "persisted-hdr.safetensors",
                    preset: "hdr",
                    driveSource: "Upload",
                    driveData: "visual",
                    driveMediaKinds: ["image", "video"],
                    stage: -1,
                    strength: 1,
                    attentionStrength: 1,
                    controlType: "none",
                    hdr: true,
                    driveMedia: null,
                },
            ],
            stages: [
                minimalStage({
                    model: "wan.safetensors",
                    modelProfileId: "wan-profile",
                }),
            ],
        });

        expect(
            deriveAuthoringDiagnostics([staleWan, minimalClip()], {
                catalog: catalogWithWan(),
            }).map(({ code }) => code),
        ).not.toContain("mixed-hdr-timeline-unsupported");
    });

    it("ignores skipped clips and HDR entries targeting skipped stages", () => {
        const inactiveHdr = minimalClip({
            icLoras: [
                {
                    lora: "ltx-ic-lora-hdr.safetensors",
                    preset: "hdr",
                    driveSource: "Upload",
                    driveData: "visual",
                    driveMediaKinds: ["image", "video"],
                    stage: 1,
                    strength: 1,
                    attentionStrength: 1,
                    controlType: "none",
                    hdr: true,
                    driveMedia: null,
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

    it("does not diagnose architectures after the first skipped clip", () => {
        const catalog = fakeArchitectureCatalog();
        const fakeClip = minimalClip({
            architecture: "test-video",
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
            architecture: "removed-architecture",
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
            architecture: "test-video",
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
            globalRefineMode: true,
        }).map((item) => item.code);

        expect(diagnostics).not.toEqual(
            expect.arrayContaining([
                "audio.reuse.requires_three_stages",
                "prompt-relay-dynamic-length-unsupported",
                "retake-frame-references-unsupported",
                "retake-source-required",
                "mixed-hdr-timeline-unsupported",
            ]),
        );
    });
});
