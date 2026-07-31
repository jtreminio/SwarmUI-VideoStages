import { describe, expect, it } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
    testSourceOnlyArchitecture,
} from "../__test_helpers__/architectureFixtures";
import {
    icLoraFixture,
    minimalClip,
    minimalRef,
    minimalStage,
    sourceVideoFixture,
} from "../__test_helpers__/clipFixtures";
import type { Clip } from "../types";
import { CONDITIONAL_RULE_CODES } from "./conditionalRules";
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

const wanCatalog = (): ArchitectureModelCatalog => {
    const models = combinedCatalog();
    const template = models.architectures.find((entry) => entry.id === "ltx2");
    if (!template) throw new Error("missing architecture template");
    const wan = structuredClone(template);
    wan.id = "wan22";
    wan.label = "WAN 2.2";
    wan.capabilities.stage = wan.capabilities.stage.filter(
        (capability) => capability !== "ic-lora",
    );
    wan.capabilities.clip = wan.capabilities.clip.filter(
        (capability) =>
            capability !== "prompt-relay" &&
            capability !== "reference-framing" &&
            capability !== "audio-reuse",
    );
    wan.capabilities.upscaleModes = ["pixel"];
    models.architectures.push(wan);
    models.entries.push({
        value: "wan-14b.safetensors",
        label: "WAN 14B",
        architectureId: "wan22",
        modelProfileId: "wan22-i2v-14b",
        modelClassId: "wan-i2v-14b",
        compatibilityClassId: "wan-video",
        entryAbilities: ["text", "image"],
        entryModes: ["image-to-video", "source-video", "refine-video"],
    });
    return models;
};

const hostVideoCatalog = (): ArchitectureModelCatalog => {
    const models = combinedCatalog();
    const template = models.architectures.find((entry) => entry.id === "ltx2");
    if (!template) throw new Error("missing architecture template");
    const host = structuredClone(template);
    host.id = "host-video";
    host.label = "Host Video";
    host.capabilities.clip = ["prompts", "source-video"];
    host.capabilities.stage = [
        "image-input",
        "video-input",
        "pixel-upscale",
        "lora",
    ];
    host.capabilities.upscaleModes = ["pixel"];
    host.capabilities.audioSourceKinds = ["Disabled"];
    models.architectures.push(host);
    models.entries.push({
        value: "host-video.safetensors",
        label: "Host Video",
        architectureId: "host-video",
        modelProfileId: "host-video",
        modelClassId: "host-video",
        compatibilityClassId: "host-video",
        entryAbilities: ["text", "image"],
        entryModes: ["text-to-video", "image-to-video", "source-video"],
    });
    return models;
};

describe("architecture diagnostics", () => {
    it("reports one precise model error when Stage 0 is unresolved", () => {
        const clip = minimalClip({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
            stages: [minimalStage({ model: "removed-model.safetensors" })],
        });

        const codes = deriveArchitectureDiagnostics(
            [clip],
            combinedCatalog(),
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

        const matching = deriveArchitectureDiagnostics(
            [clip],
            combinedCatalog(),
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

        const diagnostics = deriveArchitectureDiagnostics(
            [clip],
            combinedCatalog(),
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

    it("warns for safely ignored WAN IC-LoRA and advanced upscale values", () => {
        const clip = minimalClip({
            architectureHint: "wan22",
            modelProfileId: "wan22-i2v-14b",
            icLoras: [icLoraFixture()],
            refFraming: "fit",
            promptWindows: [{ prompt: "later", start: 1, duration: 1 }],
            reuseAudio: true,
            stages: [
                minimalStage({
                    model: "wan-14b.safetensors",
                    modelProfileId: "wan22-i2v-14b",
                    upscale: 2,
                    upscaleMethod: "latentmodel-detail.safetensors",
                }),
            ],
        });
        const before = structuredClone(clip);

        const matching = deriveArchitectureDiagnostics(
            [clip],
            wanCatalog(),
        ).filter(({ code }) =>
            [
                "architecture.unsupported.ic-lora",
                "architecture.unsupported.upscale",
                "architecture.unsupported.reference-framing",
                "architecture.unsupported.prompt-relay",
                "architecture.unsupported.audio-reuse",
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
            refs: [minimalRef()],
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

        const matching = deriveArchitectureDiagnostics(
            [clip],
            hostVideoCatalog(),
        ).filter(({ code }) => code.startsWith("architecture.unsupported."));

        expect(matching.length).toBeGreaterThan(0);
        expect(matching.every(({ severity }) => severity === "warning")).toBe(
            true,
        );
        expect(clip).toEqual(before);
    });

    it("uses per-feature backend dispositions without architecture-name switches", () => {
        const models = hostVideoCatalog();
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
            refs: [minimalRef()],
            promptWindows: [{ prompt: "later", start: 1, duration: 1 }],
            stages: [
                minimalStage({
                    model: model.value,
                    modelProfileId: model.modelProfileId ?? "host-video",
                }),
            ],
        });

        const matching = deriveArchitectureDiagnostics([clip], models).filter(
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
    });

    it("keeps absent structural capabilities blocking", () => {
        const models = hostVideoCatalog();
        const host = models.architectures.find(
            (entry) => entry.id === "host-video",
        );
        if (!host) throw new Error("missing host fixture");
        host.capabilities.architecture = host.capabilities.architecture.filter(
            (capability) => capability !== "multi-stage",
        );
        const clip = minimalClip({
            architectureHint: host.id,
            stages: [
                minimalStage({
                    model: "host-video.safetensors",
                    modelProfileId: "host-video",
                }),
                minimalStage({
                    model: "host-video.safetensors",
                    modelProfileId: "host-video",
                }),
            ],
        });

        expect(
            deriveArchitectureDiagnostics([clip], models).find(
                ({ code }) => code === "architecture.unsupported.multi-stage",
            )?.severity,
        ).toBe("error");
    });

    it("keeps an unclassifiable WAN upscale blocking", () => {
        const clip = minimalClip({
            architectureHint: "wan22",
            modelProfileId: "wan22-i2v-14b",
            stages: [
                minimalStage({
                    model: "wan-14b.safetensors",
                    modelProfileId: "wan22-i2v-14b",
                    upscale: 2,
                    upscaleMethod: "future-upscale",
                }),
            ],
        });

        expect(
            deriveArchitectureDiagnostics([clip], wanCatalog()).find(
                ({ code }) => code === "architecture.unsupported.upscale",
            )?.severity,
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
            deriveArchitectureDiagnostics([clip], hostVideoCatalog()).find(
                ({ code }) => code === "architecture.unsupported.upscale",
            )?.severity,
        ).toBe("error");
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

    it("blocks incompatible model classes inside one architecture", () => {
        const models = combinedCatalog();
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
            deriveArchitectureDiagnostics([clip], models).map(
                ({ code }) => code,
            ),
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
            architectureHint: "none",
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
            architectureHint: "none",
            modelProfileId: "none",
            sourceVideo: sourceVideoFixture(),
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

        const codes = deriveArchitectureDiagnostics(
            [clip],
            combinedCatalog(),
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
            sourceVideo: sourceVideoFixture(),
            stages: [],
            clipLengthFromControlNet: true,
        });
        const before = structuredClone(clip);

        const codes = deriveArchitectureDiagnostics(
            [clip],
            combinedCatalog(),
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

        const validCodes = deriveArchitectureDiagnostics(
            [valid],
            combinedCatalog(),
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
            architectureHint: "test-video",
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

        const diagnostic = deriveArchitectureDiagnostics(
            [left, right],
            combinedCatalog(),
        ).find(({ code }) => code === "architecture.cross-boundary-cut-only");
        expect(diagnostic?.severity).toBe("warning");
    });

    it("uses actual WAN models for a same-architecture unsupported continue despite stale LTX hints", () => {
        const models = wanCatalog();
        const wan = models.architectures.find((entry) => entry.id === "wan22");
        if (!wan) throw new Error("missing WAN architecture");
        wan.boundaryRules.continue = {
            support: "unsupported",
            code: "wan22.boundary.continue.unsupported",
            reason: "WAN 2.2 only supports cut boundaries.",
            scope: "boundary",
            constraints: null,
        };
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

        const diagnostics = deriveArchitectureDiagnostics(clips, models);

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
            deriveArchitectureDiagnostics(
                [minimalClip({ boundaryOut: "continue" }), right],
                combinedCatalog(),
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
                        sourceVideo: sourceVideoFixture(),
                    }),
                ),
            ).toContain("architecture.cross-boundary-cut-only");
        });

        it("warns when a continue target is sourced", () => {
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
            architectureHint: "removed-architecture",
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
        expect(sourceOnly.architectureHint).toBe("removed-architecture");
    });

    it("validates same-architecture dormant stages without comparing them to none", () => {
        const sourceOnly = minimalClip({
            architectureHint: "none",
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
            architectureHint: "none",
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
            architectureHint: "test-video",
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

    it("does not diagnose a disabled clip LoRA from legacy profile metadata", () => {
        const models = combinedCatalog();
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

    it("diagnoses the sampling-stage LoRA rule only on active effective stages", () => {
        const models = combinedCatalog();
        const architecture = models.architectures[0];
        architecture.rules = [
            {
                support: "conditional",
                code: CONDITIONAL_RULE_CODES.normalLoraRequiresSamplingStage,
                reason: "Normal LoRAs require a sampling stage and cannot have nonzero weight on a samplerless passthrough.",
                scope: "stage",
                constraints: { exclusiveMinimumControl: 0 },
            },
        ];
        const clip = minimalClip({
            loras: [{ name: "normal-lora.safetensors" }],
            stages: [
                minimalStage({ control: 0, loraWeights: [1] }),
                minimalStage({
                    skipped: true,
                    control: 0,
                    loraWeights: [1],
                }),
                minimalStage({ control: 0, loraWeights: [1] }),
                minimalStage({ control: 0, loraWeights: [0] }),
            ],
        });

        const matching = deriveArchitectureDiagnostics([clip], models).filter(
            ({ code }) =>
                code === CONDITIONAL_RULE_CODES.normalLoraRequiresSamplingStage,
        );
        expect(matching).toHaveLength(1);
        expect(matching[0].message).toContain("Stage 0");

        clip.skipped = true;
        expect(
            deriveArchitectureDiagnostics([clip], models).some(
                ({ code }) =>
                    code ===
                    CONDITIONAL_RULE_CODES.normalLoraRequiresSamplingStage,
            ),
        ).toBe(false);
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
            deriveArchitectureDiagnostics([clip], models).map(
                ({ code }) => code,
            ),
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
            deriveArchitectureDiagnostics([clip], models).map(
                ({ code }) => code,
            ),
        ).not.toContain("architecture.temporal-grid-conflict");
    });

    it("keeps text entry exact when a WAN clip has a frame-1 reference", () => {
        const models = combinedCatalog();
        const template = models.architectures.find(
            (entry) => entry.id === "ltx2",
        );
        if (!template) throw new Error("missing architecture template");
        const wan = structuredClone(template);
        wan.id = "wan22";
        wan.label = "WAN 2.2";
        wan.capabilities.entryModes = [
            "text-to-video",
            "image-to-video",
            "source-video",
        ];
        models.architectures.push(wan);
        models.entries.push(
            {
                value: "wan-14b.safetensors",
                label: "WAN 14B",
                architectureId: "wan22",
                modelProfileId: "wan22-i2v-14b",
                modelClassId: "wan-i2v-14b",
                compatibilityClassId: "wan-video",
                entryAbilities: ["text", "image"],
                entryModes: ["image-to-video", "source-video", "refine-video"],
            },
            {
                value: "wan-5b.safetensors",
                label: "WAN 5B",
                architectureId: "wan22",
                modelProfileId: "wan22-ti2v-5b",
                modelClassId: "wan-ti2v-5b",
                compatibilityClassId: "wan-video",
                entryAbilities: ["text", "image"],
                entryModes: ["text-to-video", "image-to-video"],
            },
        );
        const guidedClip = (model: string, modelProfileId: string) =>
            minimalClip({
                architectureHint: "wan22",
                modelProfileId,
                refs: [minimalRef({ frame: 1 })],
                stages: [
                    minimalStage({
                        model,
                        modelProfileId,
                    }),
                ],
            });

        expect(
            deriveArchitectureDiagnostics(
                [guidedClip("wan-14b.safetensors", "wan22-i2v-14b")],
                models,
            ).map(({ code }) => code),
        ).not.toContain("architecture.entry-mode-unsupported");
        expect(
            deriveArchitectureDiagnostics(
                [guidedClip("wan-5b.safetensors", "wan22-ti2v-5b")],
                models,
            ).map(({ code }) => code),
        ).not.toContain("architecture.entry-mode-unsupported");
    });
});
