import { describe, expect, it } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
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
import { noneArchitecture } from "./none/definition";
import {
    createCapabilityViewResolver,
    reconcileSourcedClipIdentity,
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
            {
                id: noneArchitecture.id,
                label: noneArchitecture.label,
                defaultProfileId: noneArchitecture.defaultProfileId,
                capabilities: structuredClone(noneArchitecture.capabilities),
                profiles: structuredClone(noneArchitecture.profiles),
                boundaryRules: structuredClone(noneArchitecture.boundaryRules),
                rules: structuredClone(noneArchitecture.rules),
            },
        ],
        entries: [...ltx.entries, ...fake.entries],
    };
};

const fakeClip = () =>
    minimalClip({
        architecture: "test-video",
        modelProfileId: "test-profile",
        stages: [
            minimalStage({
                model: "test-video.safetensors",
                modelProfileId: "test-profile",
            }),
        ],
    });

describe("catalog-backed authoring policy", () => {
    it("uses scoped capabilities and keeps unsupported persisted values removable", () => {
        const view = createCapabilityViewResolver(catalog()).forClip(
            fakeClip(),
        );

        expect(view.decision("majorPrompt").supported).toBe(true);
        expect(view.decision("frameReferences").supported).toBe(false);
        expect(view.decision("clipAudio").supported).toBe(false);
        expect(view.authoringState("frameReferences", false)).toMatchObject({
            visible: false,
            enabled: false,
        });
        expect(view.authoringState("frameReferences", true)).toMatchObject({
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

        reconcileSourcedClipIdentity(clip, models);

        expect(clip).toMatchObject({
            architecture: "none",
            modelProfileId: "none",
        });
        const view = createCapabilityViewResolver(models).forClip(clip);
        expect(view.known).toBe(true);
        expect(view.decision("sourceVideo").supported).toBe(true);
        expect(view.decision("majorPrompt").supported).toBe(false);
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
                entityId: null,
                constraints: { minimumActiveStages: 3 },
            },
            {
                support: "conditional",
                code: "prompt-relay-dynamic-length-unsupported",
                reason: "A fixed frame count is required.",
                scope: "clip",
                entityId: null,
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

        // An explicit global Refine Video invocation supplies the footage.
        expect(
            createCapabilityViewResolver(models, { globalRefineMode: true })
                .forClip(minimalClip())
                .decision("retake").supported,
        ).toBe(true);
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
        expect(body.querySelector(".vst-audio-clip")?.className).toContain(
            "vst-capability-disabled",
        );
        expect(body.querySelector("[data-vst-prompt-add]")).toBeNull();
        expect(body.querySelector("[data-vst-ref-add]")).toBeNull();
        expect(body.querySelector("[data-vst-retake-add]")).toBeNull();
    });

    it("keeps a zero-stage source-only clip selectable and labels it plainly", () => {
        const clip = minimalClip({
            architecture: "none",
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

    it("does not treat a profile without normal-lora as Stage-LoRA support", () => {
        const models = catalog();
        const ltx = models.architectures.find((entry) => entry.id === "ltx2");
        if (!ltx) throw new Error("missing LTX architecture");
        ltx.profiles[0].capabilities = [];
        const clip = minimalClip();
        const decision = createCapabilityViewResolver(models)
            .forStage(clip, clip.stages[0])
            .decision("stageLoras");

        expect(decision.supported).toBe(false);
        expect(decision.reason).toContain("normal-LoRA");
    });

    it("repairs none identity from authored Stage 0 after source removal", () => {
        const models = catalog();
        const clip = minimalClip({
            architecture: "none",
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

        reconcileSourcedClipIdentity(clip, models);
        expect(clip).toMatchObject({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
        });

        clip.stages[0].skipped = false;
        reconcileSourcedClipIdentity(clip, models);
        expect(clip).toMatchObject({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
        });
    });

    it("restores clip identity from authored stage zero when a later stage is the first active one", () => {
        const models = catalog();
        const descriptor = models.architectures.find(
            (entry) => entry.id === "ltx2",
        );
        if (!descriptor) throw new Error("missing LTX architecture");
        descriptor.profiles.push({
            id: "ltx-alt-profile",
            label: "Synthetic alternate profile",
            capabilities: [],
            rules: [],
        });
        models.entries.push({
            value: "ltx-alt-profile-model",
            label: "Synthetic alternate model",
            compatId: "ltxv2",
            modelClassId: "lightricks-ltx-video-2-3",
            architectureId: "ltx2",
            modelProfileId: "ltx-alt-profile",
        });
        const clip = minimalClip({
            architecture: "none",
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

        reconcileSourcedClipIdentity(clip, models);

        expect(clip).toMatchObject({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
        });
    });
});
