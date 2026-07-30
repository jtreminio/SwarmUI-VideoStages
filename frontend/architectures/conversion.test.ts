import { describe, expect, it, jest } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
} from "../__test_helpers__/architectureFixtures";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import { createTimelineHistory } from "../timelineHistory";
import { modelSupportsStageEntry } from "./conversion/entryModePolicy";
import { planArchitectureConversion } from "./conversion/plan";
import {
    architectureConversionMessage,
    confirmArchitectureConversion,
} from "./conversion/presentation";

describe("architecture conversion policy", () => {
    it("preserves architecture-owned settings as dormant data", () => {
        const fake = fakeArchitectureCatalog();
        fake.architectures[0].capabilities.architecture =
            fake.architectures[0].capabilities.architecture.filter(
                (feature) => feature !== "multi-stage",
            );
        const target = {
            architectureId: "test-video",
            modelProfileId: "test-profile",
            model: "test-video.safetensors",
            capabilities: fake.architectures[0].capabilities,
            entryModes: fake.architectures[0].capabilities.entryModes,
        };
        const clip = minimalClip({
            loras: [{ name: "detail" }],
            refs: [minimalRef()],
            refFraming: "fit-green",
            promptWindows: [{ prompt: "relay", start: 0, duration: 1 }],
            stages: [minimalStage({ loraWeights: [1] }), minimalStage()],
        });

        const conversion = planArchitectureConversion(clip, target, fake);
        expect(conversion).not.toBeNull();
        const removals = conversion?.removals ?? [];
        const message = architectureConversionMessage(
            "LTX Video 2.3",
            "Test Video",
            removals,
        );

        expect(removals).toEqual([]);
        expect(message).toContain("one undoable change");
        expect(message).toContain("stay saved but dormant");
        expect(conversion?.clip).toMatchObject({
            refs: clip.refs,
            refFraming: "fit-green",
            loras: clip.loras,
            promptWindows: clip.promptWindows,
        });
    });

    it("drops the opaque payload only when the owning architecture changes", () => {
        const payload = { ltx2: { tuning: "private" } };
        const clip = minimalClip({ architecturePayload: payload });

        const converted = planArchitectureConversion(
            clip,
            {
                architectureId: "test-video",
                modelProfileId: "test-profile",
                model: "test-video.safetensors",
                capabilities:
                    testArchitectureCatalog().architectures[0].capabilities,
                entryModes:
                    fakeArchitectureCatalog().architectures[0].capabilities
                        .entryModes,
            },
            fakeArchitectureCatalog(),
        );
        // A retarget inside the owning architecture is not a change of owner.
        const retargeted = planArchitectureConversion(
            clip,
            {
                architectureId: "ltx2",
                modelProfileId: "ltx-2.3",
                model: "ltx",
                capabilities:
                    testArchitectureCatalog().architectures[0].capabilities,
                entryModes:
                    testArchitectureCatalog().architectures[0].capabilities
                        .entryModes,
            },
            testArchitectureCatalog(),
        );

        // A dormant sourced clip reads as `none`, but its authored stages still
        // belong to the architecture that wrote the payload.
        const dormant = planArchitectureConversion(
            minimalClip({
                architecture: "none",
                modelProfileId: "none",
                architecturePayload: payload,
                sourceVideo: {
                    data: "data:video/mp4;base64,AA==",
                    fileName: "source.mp4",
                    fps: 24,
                    durationSeconds: 2,
                    startSeconds: 0,
                    lengthSeconds: 2,
                },
                // Stale profile: the conversion repairs it, so it must not read
                // as "this clip has no authored owner".
                stages: [
                    minimalStage({
                        skipped: true,
                        modelProfileId: "stale-profile",
                    }),
                ],
            }),
            {
                architectureId: "ltx2",
                modelProfileId: "ltx-2.3",
                model: "ltx",
                capabilities:
                    testArchitectureCatalog().architectures[0].capabilities,
                entryModes:
                    testArchitectureCatalog().architectures[0].capabilities
                        .entryModes,
            },
            testArchitectureCatalog(),
        );

        expect(converted?.clip.architecturePayload).toBeNull();
        expect(converted?.removals).toContain("architecture-specific payload");
        expect(retargeted?.clip.architecturePayload).toEqual(payload);
        expect(retargeted?.removals).not.toContain(
            "architecture-specific payload",
        );
        expect(dormant?.clip.architecturePayload).toEqual(payload);
        expect(clip.architecturePayload).toEqual(payload);
    });

    it("uses resolved Stage-0 ownership and drops payloads with only an unresolved hint", () => {
        const payload = { owner: { private: true } };
        const catalog = testArchitectureCatalog();
        const target = {
            architectureId: "ltx2",
            modelProfileId: "ltx-2.3",
            model: "ltx",
            capabilities: catalog.architectures[0].capabilities,
            entryModes: catalog.architectures[0].capabilities.entryModes,
        };
        const staleHint = planArchitectureConversion(
            minimalClip({
                architecture: "test-video",
                modelProfileId: "test-profile",
                architecturePayload: payload,
                stages: [minimalStage({ model: "ltx" })],
            }),
            target,
            catalog,
        );
        const unresolvedHint = planArchitectureConversion(
            minimalClip({
                architecture: "ltx2",
                modelProfileId: "ltx-2.3",
                architecturePayload: payload,
                stages: [minimalStage({ model: "removed-model.safetensors" })],
            }),
            target,
            catalog,
        );

        expect(staleHint?.clip.architecturePayload).toEqual(payload);
        expect(unresolvedHint?.clip.architecturePayload).toBeNull();
        expect(unresolvedHint?.removals).toContain(
            "architecture-specific payload",
        );
    });

    it("does not apply on cancel and applies exactly once on confirm", () => {
        const apply = jest.fn(() => true);

        expect(
            confirmArchitectureConversion("convert?", apply, () => false),
        ).toBe(false);
        expect(apply).not.toHaveBeenCalled();

        expect(
            confirmArchitectureConversion("convert?", apply, () => true),
        ).toBe(true);
        expect(apply).toHaveBeenCalledTimes(1);
    });

    it("requires the host-generated root mode and accepts decoded source entry", () => {
        const source = minimalClip({
            sourceVideo: {
                data: "data:video/mp4;base64,AA==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
        });

        expect(
            modelSupportsStageEntry(
                { entryModes: ["image-to-video"] },
                source,
                0,
                "text-to-video",
            ),
        ).toBe(true);
        expect(
            modelSupportsStageEntry(
                { entryModes: ["text-to-video"] },
                source,
                0,
                "text-to-video",
            ),
        ).toBe(false);
        expect(
            modelSupportsStageEntry(
                { entryModes: ["source-video"] },
                source,
                0,
                "text-to-video",
            ),
        ).toBe(true);
        expect(
            modelSupportsStageEntry(
                { entryModes: ["refine-video"] },
                source,
                0,
                "text-to-video",
            ),
        ).toBe(true);
        const guidedText = minimalClip({ refs: [minimalRef({ frame: 1 })] });
        expect(
            modelSupportsStageEntry(
                { entryModes: ["text-to-video"] },
                guidedText,
                0,
                "text-to-video",
            ),
        ).toBe(true);
        expect(
            modelSupportsStageEntry(
                { entryModes: ["image-to-video"] },
                guidedText,
                0,
                "text-to-video",
            ),
        ).toBe(false);
    });

    it("uses model entry facts before the legacy text/image aliases", () => {
        const clip = minimalClip({
            stages: [minimalStage(), minimalStage()],
        });
        const contradictory = {
            entryAbilities: ["text"],
            entryModes: ["text-to-video", "image-to-video"],
        };

        expect(
            modelSupportsStageEntry(contradictory, clip, 0, "text-to-video"),
        ).toBe(true);
        expect(
            modelSupportsStageEntry(contradictory, clip, 1, "text-to-video"),
        ).toBe(false);
    });

    it("uses the host root mode only for the first active stage", () => {
        const textOnly = { entryModes: ["text-to-video"] };
        const imageOnly = { entryModes: ["image-to-video"] };
        const clip = minimalClip({
            stages: [minimalStage(), minimalStage()],
        });

        expect(
            modelSupportsStageEntry(textOnly, clip, 0, "text-to-video"),
        ).toBe(true);
        expect(
            modelSupportsStageEntry(imageOnly, clip, 0, "text-to-video"),
        ).toBe(false);
        expect(
            modelSupportsStageEntry(textOnly, clip, 1, "text-to-video"),
        ).toBe(false);
        expect(
            modelSupportsStageEntry(imageOnly, clip, 1, "text-to-video"),
        ).toBe(true);
    });

    it("rejects an image-only whole-clip target when the first active stage needs text entry", () => {
        const catalog = fakeArchitectureCatalog();
        catalog.entries[0].entryModes = ["image-to-video"];
        const clip = minimalClip({
            stages: [
                minimalStage({ skipped: true }),
                minimalStage({ model: "ltx" }),
            ],
        });

        expect(
            planArchitectureConversion(
                clip,
                {
                    architectureId: "test-video",
                    modelProfileId: "test-profile",
                    model: "test-video.safetensors",
                    capabilities: catalog.architectures[0].capabilities,
                    entryModes: ["image-to-video"],
                },
                catalog,
                "text-to-video",
            ),
        ).toBeNull();
    });

    it("rejects a target that cannot perform a preserved later stage", () => {
        const catalog = fakeArchitectureCatalog();
        catalog.architectures[0].capabilities.architecture =
            catalog.architectures[0].capabilities.architecture.filter(
                (capability) => capability !== "multi-stage",
            );
        catalog.entries[0].entryModes = ["text-to-video"];
        const source = minimalClip({
            stages: [minimalStage(), minimalStage()],
        });
        const before = structuredClone(source);

        const conversion = planArchitectureConversion(
            source,
            {
                architectureId: "test-video",
                modelProfileId: "test-profile",
                model: "test-video.safetensors",
                capabilities: catalog.architectures[0].capabilities,
                entryModes: ["text-to-video"],
            },
            catalog,
            "text-to-video",
        );

        expect(conversion).toBeNull();
        expect(source).toEqual(before);
    });

    it("preserves source video when an image-capable target can refine it", () => {
        const catalog = fakeArchitectureCatalog();
        catalog.entries[0].entryModes = ["image-to-video"];
        const source = minimalClip({
            sourceVideo: {
                data: "data:video/mp4;base64,AA==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
        });
        const before = structuredClone(source);

        const conversion = planArchitectureConversion(
            source,
            {
                architectureId: "test-video",
                modelProfileId: "test-profile",
                model: "test-video.safetensors",
                capabilities: catalog.architectures[0].capabilities,
                entryModes: ["image-to-video"],
            },
            catalog,
            "text-to-video",
        );
        expect(conversion?.clip.sourceVideo).toEqual(source.sourceVideo);
        expect(source).toEqual(before);
    });

    it("accepts image-capable models for decoded source refinement", () => {
        const sourced = minimalClip({
            sourceVideo: {
                data: "data:video/mp4;base64,AA==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
        });

        expect(
            modelSupportsStageEntry(
                { entryModes: ["image-to-video"] },
                sourced,
                0,
                "text-to-video",
            ),
        ).toBe(true);
    });

    it("retains clip LoRAs and dormant samplerless weights", () => {
        const catalog = testArchitectureCatalog();
        const source = minimalClip({
            loras: [{ name: "detail" }, { name: "motion" }],
            stages: [
                minimalStage({ control: 0, loraWeights: [1, 0.4] }),
                minimalStage({ control: 0.5, loraWeights: [0.7, 0] }),
            ],
        });
        const conversion = planArchitectureConversion(
            source,
            {
                architectureId: "ltx2",
                modelProfileId: "ltx-2.3",
                model: "ltx",
                capabilities: catalog.architectures[0].capabilities,
                entryModes: catalog.architectures[0].capabilities.entryModes,
            },
            catalog,
        );

        expect(conversion?.clip.loras).toEqual(source.loras);
        expect(conversion?.clip.stages[0].loraWeights).toEqual([1, 0.4]);
        expect(conversion?.clip.stages[1].loraWeights).toEqual([0.7, 0]);
        expect(conversion?.removals).toEqual([]);
        expect(source.stages[0].loraWeights).toEqual([1, 0.4]);
    });

    it("round-trips one destructive conversion through one exact undo/redo point", () => {
        const catalog = fakeArchitectureCatalog();
        const target = {
            architectureId: "test-video",
            modelProfileId: "test-profile",
            model: "test-video.safetensors",
            capabilities:
                testArchitectureCatalog().architectures[0].capabilities,
            entryModes: catalog.architectures[0].capabilities.entryModes,
        };
        const source = minimalClip({
            // Including the dropped payload: undo has to restore it too.
            architecturePayload: { ltx2: { tuning: "private" } },
            loras: [{ name: "detail" }],
            refs: [minimalRef()],
            stages: [
                minimalStage(),
                minimalStage({
                    skipped: true,
                    loraWeights: [1],
                }),
            ],
        });
        const conversion = planArchitectureConversion(source, target, catalog);
        expect(conversion).not.toBeNull();

        const before = JSON.stringify(source);
        const after = JSON.stringify(conversion?.clip);
        let carrier = before;
        const history = createTimelineHistory({
            read: () => carrier,
            write: (value) => {
                carrier = value;
            },
        });
        history.rebase();
        carrier = after;
        history.capture();

        expect(history.undo()).toBe(true);
        expect(carrier).toBe(before);
        expect(history.undo()).toBe(false);
        expect(history.redo()).toBe(true);
        expect(carrier).toBe(after);
        expect(history.redo()).toBe(false);
    });
});
