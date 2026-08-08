import { describe, expect, it } from "@jest/globals";
import {
    testArchitectureCatalog,
    testCombinedCatalog,
    testCombinedCatalogWithHostVideo,
    testCombinedCatalogWithWan,
} from "../__test_helpers__/architectureFixtures";
import {
    icLoraFixture,
    initVideoFixture,
    minimalClip,
    minimalRef,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import type { Clip } from "../types";
import { deriveArchitectureDiagnostics } from "./diagnostics";
import { ARCHITECTURE_FEATURE_LABELS } from "./generatedFeatures";
import { createCapabilityViewResolver } from "./policy";
import type { ArchitectureModelCatalog } from "./types";

const architectureDiagnosticsFor = (
    clips: readonly Clip[],
    catalog: ArchitectureModelCatalog,
) =>
    deriveArchitectureDiagnostics(clips, createCapabilityViewResolver(catalog));

describe("architecture diagnostics", () => {
    it("reports one precise model error when Stage 0 is unresolved", () => {
        const clip = minimalClip({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
            stages: [minimalStage({ model: "removed-model.safetensors" })],
        });

        const codes = architectureDiagnosticsFor(
            [clip],
            testCombinedCatalog(),
        ).map(({ code }) => code);

        expect(codes).toContain("architecture.model-unknown");
        expect(codes).not.toContain("architecture.unknown");
    });

    it("warns for stale resolved identity hints without changing the authored clip", () => {
        const clip = minimalClip({
            architectureHint: "removed-cache",
            modelProfileId: "old-profile",
            stages: [
                minimalStage({
                    model: "ltx",
                    modelProfileId: "old-stage-profile",
                }),
            ],
        });
        const before = structuredClone(clip);

        const matching = architectureDiagnosticsFor(
            [clip],
            testCombinedCatalog(),
        ).filter(({ code }) =>
            [
                "architecture.stale-identity-hint",
                "architecture.stale-profile-hint",
                "architecture.profile-mismatch",
            ].includes(code),
        );

        expect(matching).toHaveLength(3);
        expect(matching.every(({ severity }) => severity === "warning")).toBe(
            true,
        );
        expect(clip).toEqual(before);
    });

    it("resolves stale hints from the first authored model even when every stage is skipped", () => {
        const clip = minimalClip({
            architectureHint: "removed-cache",
            modelProfileId: "old-profile",
            stages: [
                minimalStage({
                    skipped: true,
                    model: "ltx",
                    modelProfileId: "old-stage-profile",
                }),
            ],
        });

        const diagnostics = architectureDiagnosticsFor(
            [clip],
            testCombinedCatalog(),
        );
        expect(
            diagnostics.find(
                ({ code }) => code === "architecture.stale-identity-hint",
            )?.severity,
        ).toBe("warning");
        expect(
            diagnostics.find(
                ({ code }) => code === "architecture.stale-profile-hint",
            )?.severity,
        ).toBe("warning");
        expect(diagnostics.map(({ code }) => code)).not.toContain(
            "architecture.unknown",
        );
    });

    it("warns for safely ignored WAN IC-LoRA and advanced values", () => {
        const clip = minimalClip({
            architectureHint: "wan22",
            modelProfileId: "wan-2.2-i2v-14b",
            icLoras: [icLoraFixture()],
            refFraming: "fit",
            promptWindows: [{ prompt: "later", start: 1, duration: 1 }],
            reuseAudio: true,
            stages: [
                minimalStage({
                    model: "wan-14b.safetensors",
                    modelProfileId: "wan-2.2-i2v-14b",
                    upscale: 2,
                    upscaleMethod: "latentmodel-detail.safetensors",
                }),
            ],
        });
        const before = structuredClone(clip);

        const matching = architectureDiagnosticsFor(
            [clip],
            testCombinedCatalogWithWan(),
        ).filter(({ code }) =>
            [
                "architecture.unsupported.ic-lora",
                "architecture.unsupported.reference-framing",
                "architecture.unsupported.prompt-relay",
                "architecture.unsupported.audio-reuse",
                "architecture.unsupported.latent-model-upscale",
            ].includes(code),
        );

        expect(matching).toHaveLength(5);
        expect(matching.every(({ severity }) => severity === "warning")).toBe(
            true,
        );
        expect(clip).toEqual(before);
    });

    it("warns for optional generic host data that backend projection safely ignores", () => {
        const clip = minimalClip({
            architectureHint: "host-video",
            modelProfileId: "host-video",
            frameRefs: [minimalRef()],
            refFraming: "fit",
            promptWindows: [{ prompt: "later", start: 1, duration: 1 }],
            reuseAudio: true,
            icLoras: [icLoraFixture()],
            stages: [
                minimalStage({
                    model: "host-video.safetensors",
                    modelProfileId: "host-video",
                    upscale: 2,
                    upscaleMethod: "latentmodel-detail.safetensors",
                }),
            ],
        });
        const before = structuredClone(clip);

        const matching = architectureDiagnosticsFor(
            [clip],
            testCombinedCatalogWithHostVideo(),
        ).filter(({ code }) => code.startsWith("architecture.unsupported."));

        expect(matching.length).toBeGreaterThan(0);
        expect(matching.every(({ severity }) => severity === "warning")).toBe(
            true,
        );
        expect(clip).toEqual(before);
    });

    it("uses per-feature backend dispositions without architecture-name switches", () => {
        const models = testCombinedCatalogWithHostVideo();
        const host = models.architectures.find(
            (entry) => entry.id === "host-video",
        );
        const model = models.entries.find(
            (entry) => entry.architectureId === "host-video",
        );
        if (!host || !model) throw new Error("missing host fixture");
        host.id = "third-party-video";
        model.architectureId = host.id;

        const clip = minimalClip({
            architectureHint: host.id,
            frameRefs: [minimalRef()],
            promptWindows: [{ prompt: "later", start: 1, duration: 1 }],
            stages: [
                minimalStage({
                    model: model.value,
                    modelProfileId: model.modelProfileId ?? "host-video",
                }),
            ],
        });

        const matching = architectureDiagnosticsFor([clip], models).filter(
            ({ code }) => code.startsWith("architecture.unsupported."),
        );
        expect(matching).toEqual(
            expect.arrayContaining([
                expect.objectContaining({
                    code: "architecture.unsupported.frame-references",
                    severity: "warning",
                }),
                expect.objectContaining({
                    code: "architecture.unsupported.prompt-relay",
                    severity: "warning",
                }),
            ]),
        );
        // Pinning the whole sentence, not just the label: a re-hardcoded copy of
        // the current label would still satisfy a `toContain` against the map.
        expect(
            matching.find(
                ({ code }) => code === "architecture.unsupported.prompt-relay",
            )?.message,
        ).toBe(
            `Clip 0 has ${ARCHITECTURE_FEATURE_LABELS.promptRelay} saved, ` +
                "but its architecture cannot use it. Generation will ignore it " +
                "and keep the authored setting.",
        );
    });

    it("keeps an unclassifiable WAN upscale blocking", () => {
        const clip = minimalClip({
            architectureHint: "wan22",
            modelProfileId: "wan-2.2-i2v-14b",
            stages: [
                minimalStage({
                    model: "wan-14b.safetensors",
                    modelProfileId: "wan-2.2-i2v-14b",
                    upscale: 2,
                    upscaleMethod: "future-upscale",
                }),
            ],
        });

        expect(
            architectureDiagnosticsFor(
                [clip],
                testCombinedCatalogWithWan(),
            ).find(({ code }) => code === "architecture.unsupported.upscale")
                ?.severity,
        ).toBe("error");
    });

    it("keeps an unclassifiable generic host upscale blocking", () => {
        const clip = minimalClip({
            architectureHint: "host-video",
            modelProfileId: "host-video",
            stages: [
                minimalStage({
                    model: "host-video.safetensors",
                    modelProfileId: "host-video",
                    upscale: 2,
                    upscaleMethod: "future-upscale",
                }),
            ],
        });

        expect(
            architectureDiagnosticsFor(
                [clip],
                testCombinedCatalogWithHostVideo(),
            ).find(({ code }) => code === "architecture.unsupported.upscale")
                ?.severity,
        ).toBe("error");
    });

    it("warns that two host-video clips can only be joined with a cut", () => {
        const hostStage = () =>
            minimalStage({
                model: "host-video.safetensors",
                modelProfileId: "host-video",
            });
        const clips = [
            minimalClip({
                architectureHint: "host-video",
                modelProfileId: "host-video",
                boundaryOut: "continue",
                stages: [hostStage()],
            }),
            minimalClip({
                architectureHint: "host-video",
                modelProfileId: "host-video",
                stages: [hostStage()],
            }),
        ];

        expect(
            architectureDiagnosticsFor(
                clips,
                testCombinedCatalogWithHostVideo(),
            ).map(({ code }) => code),
        ).toContain("architecture.boundary-unsupported");
    });

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

        const diagnostics = architectureDiagnosticsFor(
            [clip],
            testCombinedCatalog(),
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

    it("blocks incompatible model classes inside one architecture", () => {
        const models = testCombinedCatalog();
        const incompatible = models.entries.find(
            (entry) => entry.value === "ltx",
        );
        if (!incompatible) throw new Error("missing LTX alias");
        incompatible.compatibilityClassId = "other-ltx-family";
        const clip = minimalClip({
            stages: [
                minimalStage({ model: "ltx-2.3.safetensors" }),
                minimalStage({ model: "ltx" }),
            ],
        });

        expect(
            architectureDiagnosticsFor([clip], models).map(({ code }) => code),
        ).toContain("architecture.mixed-stage");
    });

    it("reports preserved length flags the clip's own state cannot honor", () => {
        const clip = minimalClip({
            audioSource: "Native",
            clipLengthFromAudio: true,
            clipLengthFromControlNet: true,
            stages: [minimalStage({ model: "ltx" })],
        });
        const before = structuredClone(clip);

        const codes = architectureDiagnosticsFor(
            [clip],
            testCombinedCatalog(),
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
            architectureHint: "none",
            modelProfileId: "none",
            initVideo: initVideoFixture(),
            stages: [],
            reuseAudio: true,
        });
        const before = structuredClone(clip);

        const codes = architectureDiagnosticsFor(
            [clip],
            testCombinedCatalog(),
        ).map(({ code }) => code);

        expect(codes).toContain("architecture.unsupported.audio-reuse");
        expect(codes).not.toContain("architecture.unsupported.audio-source");
        expect(clip).toEqual(before);
    });

    it("reports unsupported audio-derived duration without rejecting None upload audio", () => {
        const clip = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            initVideo: initVideoFixture(),
            stages: [],
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "voice.wav",
            },
            saveAudioTrack: true,
            clipLengthFromAudio: true,
        });
        const before = structuredClone(clip);

        const codes = architectureDiagnosticsFor(
            [clip],
            testCombinedCatalog(),
        ).map(({ code }) => code);

        expect(codes).toContain(
            "architecture.unsupported.audio-derived-duration",
        );
        expect(codes).not.toContain("architecture.unsupported.audio-source");
        expect(codes).toContain("architecture.unsupported.audio-output");
        expect(codes).not.toContain(
            "architecture.unusable.clip-length-from-audio",
        );
        expect(clip).toEqual(before);
    });

    it("reports unsupported control-signal duration without inventing an audio-source issue", () => {
        const clip = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            initVideo: initVideoFixture(),
            stages: [],
            clipLengthFromControlNet: true,
        });
        const before = structuredClone(clip);

        const codes = architectureDiagnosticsFor(
            [clip],
            testCombinedCatalog(),
        ).map(({ code }) => code);

        expect(codes).toContain(
            "architecture.unsupported.control-signal-derived-duration",
        );
        expect(codes).not.toContain("architecture.unsupported.audio-source");
        expect(codes).not.toContain(
            "architecture.unusable.clip-length-from-control-net",
        );
        expect(clip).toEqual(before);
    });

    it("accepts LTX control-signal duration only with a canonical slot source", () => {
        const valid = minimalClip({
            clipLengthFromControlNet: true,
            icLoras: [
                icLoraFixture({
                    driveSource: "ControlNet 3",
                }),
            ],
        });

        const validCodes = architectureDiagnosticsFor(
            [valid],
            testCombinedCatalog(),
        ).map(({ code }) => code);

        expect(validCodes).not.toContain(
            "architecture.unusable.clip-length-from-control-net",
        );
        expect(validCodes).not.toContain(
            "architecture.unsupported.control-signal-derived-duration",
        );
    });

    it("reports an unknown LTX source before evaluating its duration usability", () => {
        const clip = minimalClip({
            audioSource: "future-audio-source",
            clipLengthFromAudio: true,
        });

        const codes = architectureDiagnosticsFor(
            [clip],
            testCombinedCatalog(),
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
        const models = testCombinedCatalog();
        const fake = models.architectures.find(
            (entry) => entry.id === "test-video",
        );
        if (!fake) throw new Error("missing fake architecture");
        fake.capabilities.features = [];
        const clip = minimalClip({
            architectureHint: "test-video",
            modelProfileId: "test-profile",
            loras: [{ name: "detail" }],
            prompt: "persisted major prompt",
            refFraming: "fit",
            frameRefs: [
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

        const codes = architectureDiagnosticsFor([clip], models).map(
            ({ code }) => code,
        );

        expect(codes).toEqual(
            expect.arrayContaining([
                "architecture.unsupported.frame-references",
                "architecture.unsupported.reference-framing",
                "architecture.unsupported.prompt-relay",
                "architecture.unsupported.audio-derived-duration",
            ]),
        );
        expect(codes).not.toContain(
            "architecture.unusable.clip-length-from-audio",
        );
        expect(clip).toEqual(before);
    });

    it("warns when cross-architecture non-cut joins will execute as cuts", () => {
        const left = minimalClip({ boundaryOut: "continue" });
        const right = minimalClip({
            architectureHint: "test-video",
            modelProfileId: "test-profile",
            stages: [
                minimalStage({
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                }),
            ],
        });

        const diagnostic = architectureDiagnosticsFor(
            [left, right],
            testCombinedCatalog(),
        ).find(({ code }) => code === "architecture.cross-boundary-cut-only");
        expect(diagnostic?.severity).toBe("warning");
    });

    it("uses actual WAN models for a same-architecture unsupported continue despite stale LTX hints", () => {
        const models = testCombinedCatalogWithWan();
        const staleWanClip = (boundaryOut: Clip["boundaryOut"]) =>
            minimalClip({
                architectureHint: "ltx2",
                modelProfileId: "ltx-2.3",
                boundaryOut,
                stages: [
                    minimalStage({
                        model: "wan-14b.safetensors",
                        modelProfileId: "ltx-2.3",
                    }),
                ],
            });
        const clips = [staleWanClip("continue"), staleWanClip("cut")];
        const before = structuredClone(clips);

        const diagnostics = architectureDiagnosticsFor(clips, models);

        expect(
            diagnostics.filter(
                ({ code }) => code === "architecture.boundary-unsupported",
            ),
        ).toEqual([
            expect.objectContaining({
                severity: "warning",
                clipIdx: 0,
            }),
        ]);
        expect(diagnostics.map(({ code }) => code)).not.toContain(
            "architecture.cross-boundary-cut-only",
        );
        expect(clips).toEqual(before);
    });

    describe("persisted joins that only the boundary constraints reject", () => {
        const codesFor = (right: Clip): string[] =>
            architectureDiagnosticsFor(
                [minimalClip({ boundaryOut: "continue" }), right],
                testCombinedCatalog(),
            ).map(({ code }) => code);

        it("accepts a continue into a plain generated neighbour", () => {
            expect(codesFor(minimalClip())).not.toContain(
                "architecture.boundary-unsupported",
            );
        });

        it("warns that a continue into a source-only skipped-stage clip becomes a cut", () => {
            expect(
                codesFor(
                    minimalClip({
                        stages: [minimalStage({ skipped: true })],
                        initVideo: initVideoFixture(),
                    }),
                ),
            ).toContain("architecture.cross-boundary-cut-only");
        });

        it("warns when a continue target is init-video", () => {
            expect(
                codesFor(minimalClip({ initVideo: initVideoFixture() })),
            ).toContain("architecture.boundary-unsupported");
        });

        it("blocks a continue into a first keyframe", () => {
            expect(
                codesFor(
                    minimalClip({ frameRefs: [minimalRef({ frame: 1 })] }),
                ),
            ).toContain("architecture.boundary-unsupported");
        });
    });

    it("retains and diagnoses a persisted source-only architecture mismatch", () => {
        const sourceOnly = minimalClip({
            architectureHint: "removed-architecture",
            modelProfileId: "removed-profile",
            initVideo: {
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
            architectureDiagnosticsFor([sourceOnly], testCombinedCatalog()).map(
                ({ code }) => code,
            ),
        ).toEqual(["architecture.source-only-requires-none"]);
        expect(sourceOnly.architectureHint).toBe("removed-architecture");
    });

    it("validates same-architecture dormant stages without comparing them to none", () => {
        const sourceOnly = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            initVideo: {
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
            architectureDiagnosticsFor([sourceOnly], testCombinedCatalog()),
        ).toEqual([]);
    });

    it("diagnoses mixed architectures among dormant source-only stages", () => {
        const sourceOnly = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            initVideo: {
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
            architectureDiagnosticsFor([sourceOnly], testCombinedCatalog()).map(
                ({ code }) => code,
            ),
        ).toEqual(["architecture.mixed-stage"]);
    });

    it("does not diagnose a disabled clip LoRA from legacy profile metadata", () => {
        const models = testCombinedCatalog();
        const clip = minimalClip({
            loras: [{ name: "normal-lora.safetensors" }],
            stages: [minimalStage({ model: "ltx", loraWeights: [0] })],
        });

        const codes = architectureDiagnosticsFor([clip], models).map(
            ({ code }) => code,
        );
        expect(codes).not.toContain(
            "architecture.unsupported.stage-loras-profile",
        );
        expect(codes).not.toContain("architecture.unsupported.stage-loras");
    });

    it("leaves normal LoRAs on a samplerless stage undiagnosed", () => {
        const models = testCombinedCatalog();
        const clip = minimalClip({
            loras: [{ name: "normal-lora.safetensors" }],
            stages: [minimalStage({ control: 0, loraWeights: [1] })],
        });

        expect(
            architectureDiagnosticsFor([clip], models).filter(({ code }) =>
                code.includes("lora"),
            ),
        ).toEqual([]);
    });

    it("surfaces unrepresentable active temporal-grid combinations", () => {
        const models = testArchitectureCatalog();
        models.entries.push(
            {
                ...models.entries[0],
                value: "grid-50000.safetensors",
                frameGrid: 50_000,
            },
            {
                ...models.entries[0],
                value: "grid-50001.safetensors",
                frameGrid: 50_001,
            },
        );
        const clip = minimalClip({
            stages: [
                minimalStage({
                    model: "grid-50000.safetensors",
                    control: 1,
                }),
                minimalStage({
                    model: "grid-50001.safetensors",
                    control: 1,
                }),
            ],
        });

        expect(
            architectureDiagnosticsFor([clip], models).map(({ code }) => code),
        ).toContain("architecture.temporal-grid-conflict");
    });

    it("does not report temporal conflicts for a skipped clip", () => {
        const models = testArchitectureCatalog();
        models.entries.push(
            {
                ...models.entries[0],
                value: "grid-50000.safetensors",
                frameGrid: 50_000,
            },
            {
                ...models.entries[0],
                value: "grid-50001.safetensors",
                frameGrid: 50_001,
            },
        );
        const clip = minimalClip({
            skipped: true,
            stages: [
                minimalStage({
                    model: "grid-50000.safetensors",
                    control: 1,
                }),
                minimalStage({
                    model: "grid-50001.safetensors",
                    control: 1,
                }),
            ],
        });

        expect(
            architectureDiagnosticsFor([clip], models).map(({ code }) => code),
        ).not.toContain("architecture.temporal-grid-conflict");
    });
});
