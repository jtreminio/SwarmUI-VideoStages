import { describe, expect, it } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
    testSourceOnlyArchitecture,
} from "../__test_helpers__/architectureFixtures";
import {
    minimalClip,
    minimalRef,
    minimalStage,
    sourceVideoFixture,
} from "../__test_helpers__/clipFixtures";
import type { Clip } from "../types";
import { deriveArchitectureDiagnostics } from "./diagnostics";
import type { ArchitectureModelCatalog } from "./types";

const combinedCatalog = (): ArchitectureModelCatalog => {
    const ltx = testArchitectureCatalog();
    const fake = fakeArchitectureCatalog();
    return {
        source: "backend",
        architectures: [
            ...ltx.architectures,
            ...fake.architectures,
            testSourceOnlyArchitecture(),
        ],
        entries: [...ltx.entries, ...fake.entries],
    };
};

describe("architecture diagnostics", () => {
    it("checks skipped authored stages and preserves the invalid document", () => {
        const clip = minimalClip({
            stages: [
                minimalStage({ model: "ltx" }),
                minimalStage({
                    skipped: true,
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                }),
            ],
        });
        const before = structuredClone(clip);

        const diagnostics = deriveArchitectureDiagnostics(
            [clip],
            combinedCatalog(),
        );

        expect(diagnostics.map(({ code }) => code)).toContain(
            "architecture.mixed-stage",
        );
        expect(
            diagnostics.find(({ code }) => code === "architecture.mixed-stage")
                ?.message,
        ).toContain("(skipped)");
        expect(clip).toEqual(before);
    });

    it("reports preserved length flags the clip's own state cannot honor", () => {
        const clip = minimalClip({
            audioSource: "Native",
            clipLengthFromAudio: true,
            clipLengthFromControlNet: true,
            stages: [minimalStage({ model: "ltx" })],
        });
        const before = structuredClone(clip);

        const codes = deriveArchitectureDiagnostics(
            [clip],
            combinedCatalog(),
        ).map(({ code }) => code);

        expect(codes).toEqual(
            expect.arrayContaining([
                "architecture.unusable.clip-length-from-audio",
                "architecture.unusable.clip-length-from-control-net",
            ]),
        );
        expect(codes).not.toContain(
            "architecture.unsupported.audio-derived-duration",
        );
        expect(clip).toEqual(before);
    });

    it("reports captured audio reuse independently from supported clip audio", () => {
        const clip = minimalClip({
            architecture: "none",
            modelProfileId: "none",
            sourceVideo: sourceVideoFixture(),
            stages: [],
            reuseAudio: true,
        });
        const before = structuredClone(clip);

        const codes = deriveArchitectureDiagnostics(
            [clip],
            combinedCatalog(),
        ).map(({ code }) => code);

        expect(codes).toContain("architecture.unsupported.audio-reuse");
        expect(codes).not.toContain("architecture.unsupported.audio-source");
        expect(clip).toEqual(before);
    });

    it("reports unsupported audio-derived duration without rejecting None upload audio", () => {
        const clip = minimalClip({
            architecture: "none",
            modelProfileId: "none",
            sourceVideo: sourceVideoFixture(),
            stages: [],
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "voice.wav",
            },
            clipLengthFromAudio: true,
        });
        const before = structuredClone(clip);

        const codes = deriveArchitectureDiagnostics(
            [clip],
            combinedCatalog(),
        ).map(({ code }) => code);

        expect(codes).toContain(
            "architecture.unsupported.audio-derived-duration",
        );
        expect(codes).not.toContain("architecture.unsupported.audio-source");
        expect(codes).not.toContain(
            "architecture.unusable.clip-length-from-audio",
        );
        expect(clip).toEqual(before);
    });

    it("reports an unknown LTX source before evaluating its duration usability", () => {
        const clip = minimalClip({
            audioSource: "future-audio-source",
            clipLengthFromAudio: true,
        });

        const codes = deriveArchitectureDiagnostics(
            [clip],
            combinedCatalog(),
        ).map(({ code }) => code);

        expect(codes).toContain("architecture.unsupported.audio-source");
        expect(codes).not.toContain(
            "architecture.unusable.clip-length-from-audio",
        );
        expect(codes).not.toContain(
            "architecture.unsupported.audio-derived-duration",
        );
    });

    it("reports persisted unsupported settings without stripping them", () => {
        const models = combinedCatalog();
        const fake = models.architectures.find(
            (entry) => entry.id === "test-video",
        );
        if (!fake) throw new Error("missing fake architecture");
        fake.capabilities.clip = [];
        const clip = minimalClip({
            architecture: "test-video",
            modelProfileId: "test-profile",
            loras: [{ name: "detail" }],
            prompt: "persisted major prompt",
            refFraming: "fit",
            refs: [
                {
                    source: "Base",
                    uploadFileName: null,
                    uploadedImage: null,
                    frame: 1,
                    fromEnd: false,
                },
            ],
            promptWindows: [{ prompt: "relay", start: 0, duration: 1 }],
            clipLengthFromAudio: true,
            stages: [
                minimalStage({
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                    loraWeights: [1],
                    upscale: 2,
                }),
            ],
        });
        const before = structuredClone(clip);

        const codes = deriveArchitectureDiagnostics([clip], models).map(
            ({ code }) => code,
        );

        expect(codes).toEqual(
            expect.arrayContaining([
                "architecture.unsupported.frame-references",
                "architecture.unsupported.reference-framing",
                "architecture.unsupported.major-prompt",
                "architecture.unsupported.prompt-relay",
                "architecture.unsupported.stage-loras",
                "architecture.unsupported.upscale",
                "architecture.unsupported.audio-derived-duration",
            ]),
        );
        expect(codes).not.toContain(
            "architecture.unusable.clip-length-from-audio",
        );
        expect(clip).toEqual(before);
    });

    it("blocks cross-architecture non-cut joins between executable neighbors", () => {
        const left = minimalClip({ boundaryOut: "continue" });
        const right = minimalClip({
            architecture: "test-video",
            modelProfileId: "test-profile",
            stages: [
                minimalStage({
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                }),
            ],
        });

        expect(
            deriveArchitectureDiagnostics([left, right], combinedCatalog()).map(
                ({ code }) => code,
            ),
        ).toContain("architecture.cross-boundary-cut-only");
    });

    describe("persisted joins that only the boundary constraints reject", () => {
        const codesFor = (right: Clip): string[] =>
            deriveArchitectureDiagnostics(
                [minimalClip({ boundaryOut: "continue" }), right],
                combinedCatalog(),
            ).map(({ code }) => code);

        it("accepts a continue into a plain generated neighbour", () => {
            expect(codesFor(minimalClip())).not.toContain(
                "architecture.boundary-unsupported",
            );
        });

        it("blocks a continue into a clip with no active stage", () => {
            expect(
                codesFor(
                    minimalClip({
                        stages: [minimalStage({ skipped: true })],
                        sourceVideo: sourceVideoFixture(),
                    }),
                ),
            ).toContain("architecture.boundary-unsupported");
        });

        it("blocks a continue into a sourced clip", () => {
            expect(
                codesFor(minimalClip({ sourceVideo: sourceVideoFixture() })),
            ).toContain("architecture.boundary-unsupported");
        });

        it("blocks a continue into a first-frame reference", () => {
            expect(
                codesFor(minimalClip({ refs: [minimalRef({ frame: 1 })] })),
            ).toContain("architecture.boundary-unsupported");
        });
    });

    it("retains and diagnoses a persisted source-only architecture mismatch", () => {
        const sourceOnly = minimalClip({
            architecture: "removed-architecture",
            modelProfileId: "removed-profile",
            sourceVideo: {
                data: "data:video/mp4;base64,AAAA",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
            stages: [],
        });

        expect(
            deriveArchitectureDiagnostics([sourceOnly], combinedCatalog()).map(
                ({ code }) => code,
            ),
        ).toEqual(["architecture.source-only-requires-none"]);
        expect(sourceOnly.architecture).toBe("removed-architecture");
    });

    it("validates same-architecture dormant stages without comparing them to none", () => {
        const sourceOnly = minimalClip({
            architecture: "none",
            modelProfileId: "none",
            sourceVideo: {
                data: "data:video/mp4;base64,AAAA",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
            stages: [
                minimalStage({
                    skipped: true,
                    model: "ltx",
                    modelProfileId: "ltx-2.3",
                }),
                minimalStage({
                    skipped: true,
                    model: "ltx",
                    modelProfileId: "ltx-2.3",
                }),
            ],
        });

        expect(
            deriveArchitectureDiagnostics([sourceOnly], combinedCatalog()),
        ).toEqual([]);
    });

    it("diagnoses mixed architectures among dormant source-only stages", () => {
        const sourceOnly = minimalClip({
            architecture: "none",
            modelProfileId: "none",
            sourceVideo: {
                data: "data:video/mp4;base64,AAAA",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
            stages: [
                minimalStage({
                    skipped: true,
                    model: "ltx",
                    modelProfileId: "ltx-2.3",
                }),
                minimalStage({
                    skipped: true,
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                }),
            ],
        });

        expect(
            deriveArchitectureDiagnostics([sourceOnly], combinedCatalog()).map(
                ({ code }) => code,
            ),
        ).toEqual(["architecture.mixed-stage"]);
    });

    it("diagnoses multiple active stages when the architecture lacks that capability", () => {
        const models = combinedCatalog();
        const fake = models.architectures.find(
            (entry) => entry.id === "test-video",
        );
        if (!fake) throw new Error("missing fake architecture");
        fake.capabilities.architecture = fake.capabilities.architecture.filter(
            (capability) => capability !== "multi-stage",
        );
        const clip = minimalClip({
            architecture: "test-video",
            modelProfileId: "test-profile",
            stages: [
                minimalStage({
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                }),
                minimalStage({
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                }),
            ],
        });

        expect(
            deriveArchitectureDiagnostics([clip], models).map(
                ({ code }) => code,
            ),
        ).toContain("architecture.unsupported.multi-stage");
    });

    it("diagnoses normal LoRAs when the profile omits normal-lora, including skipped stages", () => {
        const models = combinedCatalog();
        const ltx = models.architectures.find((entry) => entry.id === "ltx2");
        if (!ltx) throw new Error("missing LTX architecture");
        ltx.profiles[0].capabilities = [];
        const clip = minimalClip({
            loras: [{ name: "normal-lora.safetensors" }],
            stages: [
                minimalStage({ model: "ltx", loraWeights: [1] }),
                minimalStage({
                    skipped: true,
                    model: "ltx",
                    loraWeights: [1],
                }),
            ],
        });

        expect(
            deriveArchitectureDiagnostics([clip], models).map(
                ({ code }) => code,
            ),
        ).toContain("architecture.unsupported.stage-loras-profile");
    });

    it("does not diagnose a clip LoRA disabled with zero weight on an unsupported profile", () => {
        const models = combinedCatalog();
        const ltx = models.architectures.find((entry) => entry.id === "ltx2");
        if (!ltx) throw new Error("missing LTX architecture");
        ltx.profiles[0].capabilities = [];
        const clip = minimalClip({
            loras: [{ name: "normal-lora.safetensors" }],
            stages: [minimalStage({ model: "ltx", loraWeights: [0] })],
        });

        const codes = deriveArchitectureDiagnostics([clip], models).map(
            ({ code }) => code,
        );
        expect(codes).not.toContain(
            "architecture.unsupported.stage-loras-profile",
        );
        expect(codes).not.toContain("architecture.unsupported.stage-loras");
    });

    it("diagnoses a persisted upscale method whose exact mode is unsupported", () => {
        const models = combinedCatalog();
        const ltx = models.architectures.find((entry) => entry.id === "ltx2");
        if (!ltx) throw new Error("missing LTX architecture");
        ltx.capabilities.upscaleModes = ["pixel"];
        const clip = minimalClip({
            stages: [
                minimalStage({
                    upscale: 2,
                    upscaleMethod: "latentmodel-detail.safetensors",
                    model: "ltx",
                }),
            ],
        });

        expect(
            deriveArchitectureDiagnostics([clip], models).map(
                ({ code }) => code,
            ),
        ).toContain("architecture.unsupported.upscale");
    });

    it("diagnoses host-entry incompatibility from the explicit generated entry hint", () => {
        const models = combinedCatalog();
        const fake = models.architectures.find(
            (entry) => entry.id === "test-video",
        );
        if (!fake) throw new Error("missing fake architecture");
        fake.capabilities.entryModes = ["text-to-video"];
        const clip = minimalClip({
            architecture: "test-video",
            modelProfileId: "test-profile",
            stages: [
                minimalStage({
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                }),
            ],
        });

        expect(
            deriveArchitectureDiagnostics([clip], models, "image-to-video").map(
                ({ code }) => code,
            ),
        ).toContain("architecture.entry-mode-unsupported");
        expect(
            deriveArchitectureDiagnostics([clip], models, "text-to-video").map(
                ({ code }) => code,
            ),
        ).not.toContain("architecture.entry-mode-unsupported");
    });
});
