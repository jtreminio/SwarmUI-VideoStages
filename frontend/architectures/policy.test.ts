import { describe, expect, it } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
    testSourceOnlyArchitecture,
    testWanArchitecture,
    testWanModelEntry,
} from "../__test_helpers__/architectureFixtures";
import {
    icLoraFixture,
    initVideoFixture,
    minimalClip,
    minimalRef,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import {
    clampDetailSelection,
    detailBreadcrumb,
} from "../detailStrip/panelRouter";
import { renderTimeline } from "../timelineView";
import { reconcileClipArchitectureIdentity } from "./clipIdentity";
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
    models.architectures.push(testWanArchitecture());
    models.entries.push(testWanModelEntry());
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
            architectureFeatureSupport("frameReferences", capabilities),
        ).toBe(true);

        capabilities.features = capabilities.features.filter(
            (capability) => capability !== "frameReferences",
        );
        expect(
            architectureFeatureSupport("frameReferences", capabilities),
        ).toBe(false);
    });

    it("tracks generation stages independently of frame-grid applicability", () => {
        const resolver = createCapabilityViewResolver(catalog());
        const derivedDuration = minimalClip({
            clipLengthFromControlNet: true,
        });
        expect(resolver.forClip(derivedDuration)).toMatchObject({
            frameGridResolution: { status: "not-applicable" },
            hasGenerationStage: true,
        });

        const passthrough = minimalClip({
            initVideo: initVideoFixture(),
            stages: [minimalStage({ control: 0 })],
        });
        expect(resolver.forClip(passthrough).hasGenerationStage).toBe(false);
    });

    it("uses scoped capabilities and keeps unsupported persisted values removable", () => {
        const view = createCapabilityViewResolver(catalog()).forClip(
            fakeClip(),
        );

        expect(view.decision("frameReferences").supported).toBe(false);
        expect(view.decision("referenceFraming").supported).toBe(false);
        expect(view.clipAudio.supported).toBe(false);
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
        first.capabilities.features = first.capabilities.features.filter(
            (capability) => capability !== "icLora",
        );
        second.capabilities = structuredClone(descriptor.capabilities);
        second.capabilities.features = second.capabilities.features.filter(
            (capability) => capability !== "promptRelay",
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
            icLoras: [icLoraFixture()],
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

    it("normalizes all-skipped init-video clips to the cataloged none identity", () => {
        const models = catalog();
        const clip = minimalClip({
            initVideo: {
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
        expect(view.clipAudio.supported).toBe(true);
        expect(view.decision("audioReuse").supported).toBe(false);
        expect(view.decision("audioDerivedDuration").supported).toBe(false);
        expect(view.decision("icLora").supported).toBe(false);
        expect(view.authoringState("audioReuse", true)).toMatchObject({
            visible: true,
            enabled: false,
        });
        const sourceStage = createCapabilityViewResolver(models).forStage(
            clip,
            clip.stages[0],
        );
        expect(sourceStage.decision("sampler").supported).toBe(false);
        expect(
            createCapabilityViewResolver(models)
                .forClip(minimalClip())
                .decision("icLora").supported,
        ).toBe(true);
    });

    it("gates neither prompt relay nor audio reuse on a dynamic clip length", () => {
        const models = catalog();
        const clip = minimalClip({
            clipLengthFromAudio: true,
            stages: [minimalStage(), minimalStage()],
        });
        const view = createCapabilityViewResolver(models).forClip(clip);

        // Both are the backend's runtime call now: relay re-tiles against the resolved
        // length, and an ineligible audio reuse is dropped silently.
        expect(view.decision("promptRelay").supported).toBe(true);
        expect(view.decision("audioReuse").supported).toBe(true);
    });

    it("disables the retake control on a text-to-video clip", () => {
        const models = catalog();
        const view = createCapabilityViewResolver(models).forClip(
            minimalClip(),
        );

        expect(view.decision("retake")).toMatchObject({
            supported: false,
            reason: "Retake requires an init-video clip.",
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

    it("keeps retake available alongside frame references", () => {
        const models = catalog();
        const initVideoClip = minimalClip({ initVideo: initVideoFixture() });
        expect(
            createCapabilityViewResolver(models)
                .forClip(initVideoClip)
                .decision("retake").supported,
        ).toBe(true);

        initVideoClip.frameRefs = [minimalRef()];
        expect(
            createCapabilityViewResolver(models)
                .forClip(initVideoClip)
                .decision("retake"),
        ).toMatchObject({ supported: true, code: "" });
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
        clip.frameRefs = [minimalRef()];
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

    it("drops the relay and retake lanes when no clip architecture offers them", () => {
        const body = document.createElement("div");
        renderTimeline(body, [fakeClip()], {
            capabilities: createCapabilityViewResolver(catalog()),
        });

        expect(body.querySelector(".vst-minor-lane")).toBeNull();
        expect(body.querySelector(".vst-retake-lane")).toBeNull();
        expect(body.querySelector(".vst-head-tag-relay")).toBeNull();
        expect(body.querySelector(".vst-head-tag-retake")).toBeNull();
        expect(body.querySelector(".vst-track-prompt")?.className).toContain(
            "vst-no-relay",
        );
        expect(body.querySelector(".vst-track-video")?.className).toContain(
            "vst-no-retake",
        );
    });

    it("leaves a blank slot for the clips an offered lane skips", () => {
        const body = document.createElement("div");
        renderTimeline(body, [minimalClip(), fakeClip()], {
            capabilities: createCapabilityViewResolver(catalog()),
        });

        for (const selector of [".vst-minor-lane", ".vst-retake-lane"]) {
            const lanes = body.querySelectorAll(selector);
            expect(lanes.length).toBe(1);
            expect(lanes[0].getAttribute("data-clip-idx")).toBe("0");
        }
        expect(body.querySelector(".vst-minor-lane")?.className).not.toContain(
            "vst-capability-disabled",
        );
        // LTX offers retake; this clip has no init video, so it keeps a disabled
        // lane — and says what is missing rather than blaming the architecture.
        const retakeLane = body.querySelector(".vst-retake-lane");
        expect(retakeLane?.className).toContain("vst-capability-disabled");
        expect(retakeLane?.getAttribute("title")).toBe(
            "Retake requires an init-video clip.",
        );
    });

    it("drops the clip audio row while keeping the timeline audio tracks", () => {
        const body = document.createElement("div");
        renderTimeline(body, [fakeClip()], {
            capabilities: createCapabilityViewResolver(catalog()),
        });

        expect(body.querySelector(".vst-audio-clip")).toBeNull();
        expect(body.querySelector(".vst-head-tag-src")).toBeNull();
        expect(body.querySelector(".vst-track-audio")?.className).toContain(
            "vst-no-clip-audio",
        );
        // Audio mixed outside the video model stays authorable regardless.
        expect(
            body.querySelector(".vst-audio-track-lane-blank"),
        ).not.toBeNull();
        // Nothing above or below it, so the lone add-lane is centred.
        expect(body.querySelector(".vst-track-audio")?.className).toContain(
            "vst-audio-solo-lane",
        );
        expect(
            body.querySelector(".vst-head-tag-track[data-vst-audio-track-add]"),
        ).not.toBeNull();
    });

    it("keeps the clip audio row for the clips whose model carries audio", () => {
        const body = document.createElement("div");
        renderTimeline(body, [minimalClip(), fakeClip()], {
            capabilities: createCapabilityViewResolver(catalog()),
        });

        const lanes = body.querySelectorAll(".vst-audio-clip");
        expect(lanes.length).toBe(1);
        expect(lanes[0].getAttribute("data-clip-idx")).toBe("0");
        expect(body.querySelector(".vst-head-tag-src")).not.toBeNull();
        expect(body.querySelector(".vst-track-audio")?.className).not.toContain(
            "vst-no-clip-audio",
        );
    });

    it("keeps a zero-stage source-only clip selectable and labels it plainly", () => {
        const clip = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            initVideo: {
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
            architectureLabel: "WAN Video",
            known: true,
        });
        expect(clipView.decision("icLora").supported).toBe(false);
        expect(stageView.decision("sampler").supported).toBe(true);
        expect(stageView.decision("scheduler").supported).toBe(true);
        expect(clip).toEqual(before);
    });

    it("keeps stage LoRA authoring enabled on a samplerless stage", () => {
        const models = catalog();
        const clip = minimalClip({
            loras: [{ name: "persisted.safetensors" }],
            stages: [minimalStage({ control: 0, loraWeights: [1] })],
        });
        const resolver = createCapabilityViewResolver(models);

        expect(
            resolver.forStage(clip, clip.stages[0]).decision("stageLoras"),
        ).toMatchObject({ supported: true, code: "" });
        expect(
            resolver
                .forStage(clip, clip.stages[0])
                .authoringState("stageLoras", true),
        ).toMatchObject({ visible: true, enabled: true });
    });

    it("repairs none identity from authored Stage 0 after source removal", () => {
        const models = catalog();
        const clip = minimalClip({
            architectureHint: "none",
            modelProfileId: "none",
            initVideo: null,
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
