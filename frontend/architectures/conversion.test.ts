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
import { architectureSupportsClipStart } from "./conversion/entryModePolicy";
import { planArchitectureConversion } from "./conversion/plan";
import {
    architectureConversionMessage,
    confirmArchitectureConversion,
} from "./conversion/presentation";

describe("architecture conversion policy", () => {
    it("summarizes destructive architecture-owned changes", () => {
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
        };
        const clip = minimalClip({
            refs: [minimalRef()],
            promptWindows: [{ prompt: "relay", start: 0, duration: 1 }],
            stages: [
                minimalStage({ loras: [{ name: "detail", weight: 1 }] }),
                minimalStage(),
            ],
        });

        const conversion = planArchitectureConversion(clip, target, fake);
        expect(conversion).not.toBeNull();
        const removals = conversion?.removals ?? [];
        const message = architectureConversionMessage(
            "LTX Video 2.3",
            "Test Video",
            removals,
        );

        expect(removals).toEqual(
            expect.arrayContaining([
                "1 later authored stage",
                "1 frame reference",
                "1 stage LoRA",
                "1 relay prompt",
            ]),
        );
        expect(message).toContain("one undoable change");
        expect(message).toContain("This removes:");
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

    it("gates source and generated starts from catalog entry modes", () => {
        const ltx = testArchitectureCatalog().architectures[0].capabilities;
        const textOnly =
            fakeArchitectureCatalog().architectures[0].capabilities;
        textOnly.entryModes = ["text-to-video"];
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
            architectureSupportsClipStart(ltx, source, "text-to-video"),
        ).toBe(true);
        expect(
            architectureSupportsClipStart(textOnly, source, "text-to-video"),
        ).toBe(false);
        expect(
            architectureSupportsClipStart(
                textOnly,
                minimalClip(),
                "text-to-video",
            ),
        ).toBe(true);
    });

    it("round-trips one destructive conversion through one exact undo/redo point", () => {
        const catalog = fakeArchitectureCatalog();
        const target = {
            architectureId: "test-video",
            modelProfileId: "test-profile",
            model: "test-video.safetensors",
            capabilities:
                testArchitectureCatalog().architectures[0].capabilities,
        };
        const source = minimalClip({
            refs: [minimalRef()],
            stages: [
                minimalStage(),
                minimalStage({
                    skipped: true,
                    loras: [{ name: "detail", weight: 1 }],
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
        history.syncBaseline();
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
