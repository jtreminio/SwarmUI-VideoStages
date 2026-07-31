import { describe, expect, it } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
    testSourceOnlyArchitecture,
} from "../__test_helpers__/architectureFixtures";
import {
    hdrIcLoraFixture,
    minimalClip,
    minimalRef,
    minimalStage,
    sourceVideoFixture,
} from "../__test_helpers__/clipFixtures";
import {
    clampDetailSelection,
    detailBreadcrumb,
} from "../detailStrip/panelRouter";
import { renderTimeline } from "../timelineView";
import type { Clip } from "../types";
import { reconcileClipArchitectureIdentity } from "./clipIdentity";
import { CONDITIONAL_RULE_CODES } from "./conditionalRules";
import {
    architectureFeatureSupport,
    createCapabilityViewResolver,
} from "./policy";
import type { ArchitectureModelCatalog } from "./types";

const catalog = (): ArchitectureModelCatalog => {
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

const catalogWithWan = (): ArchitectureModelCatalog => {
    const models = catalog();
    const ltx = models.architectures.find((entry) => entry.id === "ltx2");
    if (!ltx) throw new Error("missing LTX architecture");
    ltx.capabilities.upscaleModes = ["model"];
    const wan = structuredClone(ltx);
    wan.id = "wan22";
    wan.label = "WAN 2.2";
    wan.capabilities.stage = wan.capabilities.stage.filter(
        (capability) =>
            capability !== "lora" &&
            capability !== "ic-lora" &&
            capability !== "hdr",
    );
    wan.capabilities.upscaleModes = ["pixel"];
    models.architectures.push(wan);
    models.entries.push({
        value: "wan-14b.safetensors",
        label: "WAN 14B",
        architectureId: "wan22",
        modelProfileId: "wan22-i2v-14b",
        modelClassId: "wan-i2v",
        compatibilityClassId: "wan-video",
        entryModes: ["image-to-video", "source-video", "refine-video"],
    });
    return models;
};

const fakeClip = () =>
    minimalClip({
        architectureHint: "test-video",
        modelProfileId: "test-profile",
        stages: [
            minimalStage({
                model: "test-video.safetensors",
                modelProfileId: "test-profile",
            }),
        ],
    });

describe("catalog-backed authoring policy", () => {
    it("reuses clip, stage, and seam views within one resolver snapshot", () => {
        const resolver = createCapabilityViewResolver(catalog());
        const left = minimalClip();
        const clips = [left, minimalClip()];

        expect(resolver.forClip(left)).toBe(resolver.forClip(left));
        expect(resolver.forStage(left, left.stages[0])).toBe(
            resolver.forStage(left, left.stages[0]),
        );
        expect(resolver.forBoundaryIndex(clips, 0)).toBe(
            resolver.forBoundaryIndex(clips, 0),
        );

        expect(createCapabilityViewResolver(catalog()).forClip(left)).not.toBe(
            resolver.forClip(left),
        );
    });

    it("requires both halves of the frame-reference contract", () => {
        const capabilities = structuredClone(
            catalog().architectures[0].capabilities,
        );
        expect(
            architectureFeatureSupport("frameReferences", { capabilities }),
        ).toBe(true);

        capabilities.clip = capabilities.clip.filter(
            (capability) => capability !== "references",
        );
        expect(
            architectureFeatureSupport("frameReferences", { capabilities }),
        ).toBe(false);

        capabilities.clip.push("references");
        capabilities.stage = capabilities.stage.filter(
            (capability) => capability !== "frame-references",
        );
        expect(
            architectureFeatureSupport("frameReferences", { capabilities }),
        ).toBe(false);
    });

    it("uses scoped capabilities and keeps unsupported persisted values removable", () => {
        const view = createCapabilityViewResolver(catalog()).forClip(
            fakeClip(),
        );

        expect(view.decision("majorPrompt").supported).toBe(true);
        expect(view.decision("frameReferences").supported).toBe(false);
        expect(view.decision("referenceFraming").supported).toBe(false);
        expect(view.decision("clipAudio").supported).toBe(false);
        expect(view.authoringState("frameReferences", false)).toMatchObject({
            visible: false,
            enabled: false,
        });
        expect(view.authoringState("frameReferences", true)).toMatchObject({
            visible: true,
            enabled: false,
        });
        expect(
            createCapabilityViewResolver(catalog())
                .forClip(minimalClip())
                .decision("referenceFraming").supported,
        ).toBe(true);
    });

    it("uses typed model narrowing and intersects every active stage model", () => {
        const models = catalog();
        const descriptor = models.architectures.find(
            (entry) => entry.id === "ltx2",
        );
        const first = models.entries.find(
            (entry) => entry.value === "ltx-2.3.safetensors",
        );
        const second = models.entries.find((entry) => entry.value === "ltx");
        if (!descriptor || !first || !second) {
            throw new Error("missing LTX test model facts");
        }
        first.enhancements = { referencePositions: [] };
        first.capabilities = structuredClone(descriptor.capabilities);
        first.capabilities.stage = first.capabilities.stage.filter(
            (capability) => capability !== "ic-lora",
        );
        second.capabilities = structuredClone(descriptor.capabilities);
        second.capabilities.clip = second.capabilities.clip.filter(
            (capability) => capability !== "prompt-relay",
        );
        const clip = minimalClip({
            stages: [
                minimalStage({ model: first.value }),
                minimalStage({ model: second.value }),
            ],
        });
        const resolver = createCapabilityViewResolver(models);

        expect(
            resolver.forStage(clip, clip.stages[0]).decision("stageLoras"),
        ).toMatchObject({ supported: true });
        expect(resolver.forClip(clip).decision("icLora").supported).toBe(false);
        expect(resolver.forClip(clip).decision("promptRelay").supported).toBe(
            false,
        );
    });

    it("does not use a persisted architecture hint when Stage 0 is unresolved", () => {
        const clip = minimalClip({
            architectureHint: "ltx2",
            icLoras: [hdrIcLoraFixture()],
            stages: [
                minimalStage({
                    model: "removed-model.safetensors",
                    modelProfileId: "removed-profile",
                }),
            ],
        });

        const view = createCapabilityViewResolver(catalog()).forClip(clip);

        expect(view.architectureId).toBe("unsupported");
        expect(view.known).toBe(false);
        expect(view.decision("icLora").supported).toBe(false);
        expect(view.authoringState("icLora", true)).toMatchObject({
            visible: true,
            enabled: false,
        });
    });

    it("normalizes all-skipped sourced clips to the cataloged none identity", () => {
        const models = catalog();
        const clip = minimalClip({
            sourceVideo: {
                data: "data:video/mp4;base64,AA==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
            stages: [minimalStage({ skipped: true })],
        });

        reconcileClipArchitectureIdentity(clip, models);

        expect(clip).toMatchObject({
            architectureHint: "none",
            modelProfileId: "none",
        });
        const view = createCapabilityViewResolver(models).forClip(clip);
        expect(view.known).toBe(true);
        expect(view.decision("sourceVideo").supported).toBe(true);
        expect(view.decision("majorPrompt").supported).toBe(false);
        expect(view.decision("clipAudio").supported).toBe(true);
        expect(view.decision("audioReuse").supported).toBe(false);
        expect(view.decision("audioDerivedDuration").supported).toBe(false);
        expect(view.decision("controlSignalDerivedDuration").supported).toBe(
            false,
        );
        expect(view.authoringState("audioReuse", true)).toMatchObject({
            visible: true,
            enabled: false,
        });
        const sourceStage = createCapabilityViewResolver(models).forStage(
            clip,
            clip.stages[0],
        );
        expect(sourceStage.upscaleModes).toEqual([]);
        expect(sourceStage.decision("sampler").supported).toBe(false);
        expect(
            createCapabilityViewResolver(models)
                .forClip(minimalClip())
                .decision("controlSignalDerivedDuration").supported,
        ).toBe(true);
    });

    it("evaluates conditional prompt and audio-reuse rules", () => {
        const models = catalog();
        const descriptor = models.architectures.find(
            (entry) => entry.id === "ltx2",
        );
        if (!descriptor) {
            throw new Error("missing LTX test descriptor");
        }
        descriptor.rules = [
            {
                support: "conditional",
                code: "audio.reuse.requires_three_stages",
                reason: "Three active stages are required.",
                scope: "clip",
                constraints: { minimumActiveStages: 3 },
            },
            {
                support: "conditional",
                code: "prompt-relay-dynamic-length-unsupported",
                reason: "A fixed frame count is required.",
                scope: "clip",
                constraints: { requiresFixedFrameCount: true },
            },
        ];
        const clip = minimalClip({
            clipLengthFromAudio: true,
            stages: [minimalStage(), minimalStage()],
        });
        const view = createCapabilityViewResolver(models).forClip(clip);

        expect(view.decision("promptRelay")).toMatchObject({
            supported: false,
            reason: "A fixed frame count is required.",
        });
        expect(view.decision("audioReuse")).toMatchObject({
            supported: false,
            reason: "Three active stages are required.",
        });

        clip.clipLengthFromAudio = false;
        clip.stages.push(minimalStage());
        const eligible = createCapabilityViewResolver(models).forClip(clip);
        expect(eligible.decision("promptRelay").supported).toBe(true);
        expect(eligible.decision("audioReuse").supported).toBe(true);
    });

    it("disables the retake control on a text-to-video clip", () => {
        const models = catalog();
        const view = createCapabilityViewResolver(models).forClip(
            minimalClip(),
        );

        expect(view.decision("retake")).toMatchObject({
            supported: false,
            reason: "Retake requires source footage.",
        });
        // Absent and unsupported: never offered for authoring.
        expect(view.authoringState("retake", false)).toMatchObject({
            visible: false,
            enabled: false,
        });
        // Persisted and unsupported: still visible so it can be removed.
        expect(view.authoringState("retake", true)).toMatchObject({
            visible: true,
            enabled: false,
        });
    });

    it("routes the retake/reference exclusion through the same decision", () => {
        const models = catalog();
        const sourced = minimalClip({ sourceVideo: sourceVideoFixture() });
        expect(
            createCapabilityViewResolver(models)
                .forClip(sourced)
                .decision("retake").supported,
        ).toBe(true);

        sourced.refs = [minimalRef()];
        expect(
            createCapabilityViewResolver(models)
                .forClip(sourced)
                .decision("retake"),
        ).toMatchObject({
            supported: false,
            reason: "Retake and frame references are mutually exclusive.",
        });
    });

    it("routes timeline HDR uniformity through the same decision", () => {
        const models = catalog();
        const hdr = minimalClip({ icLoras: [hdrIcLoraFixture()] });
        const plain = minimalClip();

        expect(
            createCapabilityViewResolver(models, {
                timelineClips: [hdr, plain],
            })
                .forClip(hdr)
                .decision("hdr"),
        ).toMatchObject({
            supported: false,
            reason: "HDR must be uniform across the timeline.",
        });
        expect(
            createCapabilityViewResolver(models, {
                timelineClips: [hdr, structuredClone(hdr)],
            })
                .forClip(hdr)
                .decision("hdr").supported,
        ).toBe(true);
        // Without the timeline context the rule stays inert.
        expect(
            createCapabilityViewResolver(models).forClip(hdr).decision("hdr")
                .supported,
        ).toBe(true);
    });

    it("truncates the executable sequence at the first skipped clip", () => {
        const models = catalog();
        const left = minimalClip({ boundaryOut: "continue" });
        const skipped = fakeClip();
        skipped.skipped = true;
        const right = fakeClip();
        const clips = [left, skipped, right];
        const resolver = createCapabilityViewResolver(models);

        expect(resolver.executableClipIndexes(clips)).toEqual([0]);
        expect(resolver.forBoundaryIndex(clips, 0)).toMatchObject({
            leftClipIdx: 0,
            rightClipIdx: null,
            modes: ["cut", "crossfade"],
            crossArchitecture: false,
        });

        expect(left.boundaryOut).toBe("continue");
        expect(resolver.forBoundaryIndex(clips, 0).effective("continue")).toBe(
            "cut",
        );
    });

    it("renders unsupported persisted features without exposing creation gestures", () => {
        const clip = fakeClip();
        clip.prompt = "Persisted major prompt";
        clip.promptWindows = [
            { prompt: "Persisted relay", start: 0, duration: 1 },
        ];
        clip.refs = [minimalRef()];
        clip.retake = {
            startSeconds: 0,
            lengthSeconds: 1,
            strength: 0.5,
        };
        clip.audioSource = "Upload";
        clip.uploadedAudio = {
            data: "data:audio/wav;base64,AA==",
            fileName: "persisted.wav",
        };
        const body = document.createElement("div");
        renderTimeline(body, [clip], {
            capabilities: createCapabilityViewResolver(catalog()),
        });

        expect(body.querySelector(".vst-major-seg")).not.toBeNull();
        expect(body.querySelector(".vst-minor-seg")).not.toBeNull();
        expect(body.querySelector(".vst-refs-mark")).not.toBeNull();
        expect(body.querySelector(".vst-retake")).not.toBeNull();
        for (const selector of [
            ".vst-minor-lane",
            ".vst-refs-lane",
            ".vst-retake-lane",
            ".vst-audio-clip",
        ]) {
            const element = body.querySelector(selector);
            expect(element?.className).toContain("vst-capability-disabled");
            expect(element?.hasAttribute("aria-disabled")).toBe(false);
        }
        for (const selector of [
            ".vst-major-seg",
            ".vst-minor-seg",
            ".vst-refs-mark",
            ".vst-retake",
            ".vst-audio-clip",
        ]) {
            expect(
                body.querySelector(selector)?.hasAttribute("aria-disabled"),
            ).toBe(false);
        }
        expect(
            body.querySelector<HTMLElement>(".vst-minor-seg")?.title,
        ).toContain("click to inspect");
        expect(body.querySelector<HTMLElement>(".vst-retake")?.title).toContain(
            "click to inspect",
        );
        expect(
            body.querySelector(".vst-refs-mark")?.getAttribute("aria-label"),
        ).toContain("Inspect unsupported persisted reference");
        expect(
            body.querySelector(".vst-audio-clip")?.getAttribute("aria-label"),
        ).toContain("Inspect unsupported persisted audio");
        expect(body.querySelector("[data-vst-prompt-add]")).toBeNull();
        expect(body.querySelector("[data-vst-ref-add]")).toBeNull();
        expect(body.querySelector("[data-vst-retake-add]")).toBeNull();
    });

    it("keeps a zero-stage source-only clip selectable and labels it plainly", () => {
        const clip = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            sourceVideo: {
                data: "data:video/mp4;base64,AA==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
            stages: [],
        });
        const selection = clampDetailSelection(
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            [clip],
        );

        expect(selection).toEqual({
            kind: "clip",
            clipIdx: 0,
            stageIdx: 0,
        });
        expect(detailBreadcrumb(selection, [clip])).toBe(
            "Clip 0 · Source only",
        );
    });

    it("uses typed architecture LoRA support when no model narrowing exists", () => {
        const models = catalog();
        const clip = minimalClip();
        const decision = createCapabilityViewResolver(models)
            .forStage(clip, clip.stages[0])
            .decision("stageLoras");

        expect(decision.supported).toBe(true);
    });

    it("uses the actual WAN stage model instead of stale LTX identity and profile hints", () => {
        const models = catalogWithWan();
        const clip = minimalClip({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
            stages: [
                minimalStage({
                    model: "wan-14b.safetensors",
                    modelProfileId: "ltx-2.3",
                }),
            ],
        });
        const before = structuredClone(clip);
        const resolver = createCapabilityViewResolver(models);
        const clipView = resolver.forClip(clip);
        const stageView = resolver.forStage(clip, clip.stages[0]);

        expect(clipView).toMatchObject({
            architectureId: "wan22",
            architectureLabel: "WAN 2.2",
            known: true,
        });
        expect(clipView.decision("icLora").supported).toBe(false);
        expect(clipView.decision("stageLoras").supported).toBe(false);
        expect(stageView.upscaleModes).toEqual(["pixel"]);
        expect(stageView.decision("upscale").supported).toBe(true);
        expect(stageView.decision("sampler").supported).toBe(true);
        expect(stageView.decision("scheduler").supported).toBe(true);
        expect(stageView.decision("stageLoras").supported).toBe(false);
        expect(clip).toEqual(before);
    });

    it("evaluates conditional HDR state with the resolved architecture ID", () => {
        const models = catalogWithWan();
        const wan = models.architectures.find((entry) => entry.id === "wan22");
        if (!wan) throw new Error("missing WAN architecture");
        // Keep HDR authoring available so this isolates the conditional
        // behavior lookup from the basic capability check.
        wan.capabilities.stage.push("hdr");
        const wanClip = (icLoras: Clip["icLoras"] = []) =>
            minimalClip({
                architectureHint: "ltx2",
                modelProfileId: "ltx-2.3",
                icLoras,
                stages: [
                    minimalStage({
                        model: "wan-14b.safetensors",
                        modelProfileId: "ltx-2.3",
                    }),
                ],
            });
        const hdr = wanClip([hdrIcLoraFixture()]);
        const plain = wanClip();

        expect(
            createCapabilityViewResolver(models, {
                timelineClips: [hdr, plain],
            })
                .forClip(hdr)
                .decision("hdr"),
        ).toMatchObject({
            supported: true,
            rule: null,
        });
    });

    it("applies an architecture stage-control rule only to stage LoRA authoring", () => {
        const models = catalog();
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
            loras: [{ name: "persisted.safetensors" }],
            stages: [minimalStage({ control: 0, loraWeights: [1] })],
        });
        const resolver = createCapabilityViewResolver(models);
        const stageDecision = resolver
            .forStage(clip, clip.stages[0])
            .decision("stageLoras");

        expect(stageDecision).toMatchObject({
            supported: false,
            reason: architecture.rules[0].reason,
            rule: {
                code: CONDITIONAL_RULE_CODES.normalLoraRequiresSamplingStage,
            },
        });
        expect(
            resolver
                .forStage(clip, clip.stages[0])
                .authoringState("stageLoras", true),
        ).toMatchObject({ visible: true, enabled: false });
        expect(resolver.forClip(clip).decision("stageLoras").supported).toBe(
            true,
        );

        clip.stages[0].control = 0.1;
        expect(
            resolver.forStage(clip, clip.stages[0]).decision("stageLoras")
                .supported,
        ).toBe(true);
    });

    it("repairs none identity from authored Stage 0 after source removal", () => {
        const models = catalog();
        const clip = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            sourceVideo: null,
            stages: [
                minimalStage({
                    skipped: true,
                    model: "ltx",
                    modelProfileId: "ltx-2.3",
                }),
            ],
        });

        reconcileClipArchitectureIdentity(clip, models);
        expect(clip).toMatchObject({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
        });

        clip.stages[0].skipped = false;
        reconcileClipArchitectureIdentity(clip, models);
        expect(clip).toMatchObject({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
        });
    });

    it("restores clip identity from authored stage zero when a later stage is the first active one", () => {
        const models = catalog();
        const descriptor = models.architectures.find(
            (entry) => entry.id === "ltx2",
        );
        if (!descriptor) throw new Error("missing LTX architecture");
        models.entries.push({
            value: "ltx-alt-profile-model",
            label: "Synthetic alternate model",
            architectureId: "ltx2",
            modelProfileId: "ltx-alt-profile",
            modelClassId: "ltx-video-alt",
            compatibilityClassId: "ltx-video",
            entryModes: ["text-to-video", "image-to-video"],
        });
        const clip = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            stages: [
                minimalStage({
                    skipped: true,
                    model: "ltx",
                    modelProfileId: "ltx-2.3",
                }),
                minimalStage({
                    model: "ltx-alt-profile-model",
                    modelProfileId: "ltx-alt-profile",
                }),
            ],
        });

        reconcileClipArchitectureIdentity(clip, models);

        expect(clip).toMatchObject({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
        });
    });
});
