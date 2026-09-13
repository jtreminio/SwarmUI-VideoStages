import { describe, expect, it } from "@jest/globals";
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
import { planArchitectureConversion } from "./conversion/plan";

describe("architecture conversion policy", () => {
    it("discards architecture-owned settings the target cannot use", () => {
        const fake = fakeArchitectureCatalog();
        const target = {
            architectureId: "test-video",
            modelProfileId: "test-profile",
            model: "test-video.safetensors",
            capabilities: fake.architectures[0].capabilities,
            entryModes: fake.architectures[0].capabilities.entryModes,
        };
        const clip = minimalClip({
            frameRefs: [minimalRef()],
            refFraming: "fit-green",
            promptWindows: [{ prompt: "relay", start: 0, duration: 1 }],
            stages: [
                minimalStage({ loras: [{ name: "detail", weight: 1 }] }),
                minimalStage(),
            ],
        });

        const conversion = planArchitectureConversion(clip, target, fake);
        expect(conversion).not.toBeNull();

        expect(conversion).toMatchObject({
            frameRefs: [],
            refFraming: "crop",
            stages: clip.stages.map((stage) => ({ loras: stage.loras })),
            promptWindows: [],
        });
    });

    it("preserves source video when an image-capable target can refine it", () => {
        const catalog = fakeArchitectureCatalog();
        catalog.entries[0].entryModes = ["image-to-video"];
        const source = minimalClip({
            initVideo: {
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
            },
            catalog,
        );
        expect(conversion?.initVideo).toEqual(source.initVideo);
        expect(source).toEqual(before);
    });

    it("retains weighted clip LoRAs", () => {
        const catalog = testArchitectureCatalog();
        const source = minimalClip({
            stages: [
                minimalStage({
                    loras: [
                        { name: "detail", weight: 1 },
                        { name: "motion", weight: 0.4 },
                    ],
                    control: 0,
                }),
                minimalStage({ control: 0.5 }),
            ],
        });
        const conversion = planArchitectureConversion(
            source,
            {
                architectureId: "ltx2",
                modelProfileId: "ltx-2.3",
                model: "ltx",
            },
            catalog,
        );

        expect(conversion?.stages[0].loras).toEqual(source.stages[0].loras);
        expect(source.stages[0].loras).toEqual([
            { name: "detail", weight: 1 },
            { name: "motion", weight: 0.4 },
        ]);
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
            frameRefs: [minimalRef()],
            stages: [
                minimalStage({ loras: [{ name: "detail", weight: 1 }] }),
                minimalStage({ skipped: true }),
            ],
        });
        const conversion = planArchitectureConversion(source, target, catalog);
        expect(conversion).not.toBeNull();

        const before = JSON.stringify(source);
        const after = JSON.stringify(conversion);
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
