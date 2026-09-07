import { describe, expect, it } from "@jest/globals";
import { initVideoFixture } from "../__test_helpers__/clipFixtures";
import { editorHarness } from "../__test_helpers__/editor";
import { getSelection, setSelection } from "../selection";
import type { TimelineSelection } from "../types";

describe("editor selection and field rendering", () => {
    const harness = editorHarness();
    it("selects a source-only clip and exposes its uploaded video", () => {
        const root = harness.setup([
            { duration: 4, stages: [{}] },
            {
                duration: 4,
                stages: [],
                initVideo: initVideoFixture({ fileName: "source.mp4" }),
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 1,
            stageIdx: 0,
        });
        expect(
            root
                .querySelector(
                    '[data-testid="select-clip"][aria-current="true"]',
                )
                ?.closest<HTMLElement>('[data-testid="clip-card"]')?.dataset
                .clipIndex,
        ).toBe("1");
        expect(harness.section("source-video").textContent).toContain(
            "source.mp4",
        );
        expect(
            harness.field<HTMLSelectElement>(
                "source-video",
                "Video source",
                "select",
            ).value,
        ).toBe("Upload");
    });
    const selections: [TimelineSelection, string, number][] = [
        [{ kind: "clip", clipIdx: 1, stageIdx: 1 }, "stages", 1],
        [{ kind: "ref", clipIdx: 1, refIdx: 0 }, "keyframes", 0],
        [{ kind: "audio", clipIdx: 1 }, "audio", 0],
        [{ kind: "prompt-major", clipIdx: 1 }, "prompt", 0],
        [
            { kind: "prompt-minor", clipIdx: 1, windowIdx: 0 },
            "relay-prompts",
            0,
        ],
        [{ kind: "retake", clipIdx: 1 }, "retake", 0],
        [{ kind: "boundary", leftClipIdx: 0 }, "join", 0],
    ];

    it.each(
        selections,
    )("reveals %j in %s with the owning clip selected", (selection, section, index) => {
        const root = harness.setup([
            { duration: 4, stages: [{}], prompt: "First shot" },
            {
                duration: 4,
                stages: [{}, { steps: 12 }],
                prompt: "Second shot",
                frameRefs: [{ source: "Base Output", frame: 12 }],
                windows: [{ start: 1, duration: 2, prompt: "Camera moves" }],
                initVideo: initVideoFixture({
                    durationSeconds: 4,
                    lengthSeconds: 4,
                }),
                retake: { startSeconds: 1, lengthSeconds: 2, strength: 0.5 },
            },
        ]);
        setSelection(selection);

        const chosen = root.querySelector(
            '[data-testid="select-clip"][aria-current="true"]',
        );
        expect(
            chosen?.closest<HTMLElement>('[data-testid="clip-card"]')?.dataset
                .clipIndex,
        ).toBe("1");
        expect(
            root
                .querySelector(`[data-section-link="${section}"]`)
                ?.getAttribute("aria-current"),
        ).toBe("location");
        const control = harness
            .item(section, index)
            .querySelector<
                HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement
            >("input:not([type=hidden]), select, textarea");
        expect(control).toBeInstanceOf(HTMLElement);
        expect(control?.disabled).toBe(false);
        expect(
            harness.field<HTMLTextAreaElement>("prompt", "Prompt", "textarea")
                .value,
        ).toBe("Second shot");
    });

    it("moves selection ownership when editing fields in another stage", () => {
        harness.setup([{ duration: 4, stages: [{ steps: 8 }, { steps: 12 }] }]);
        const first = harness.field("stages", "Steps", "input[type=number]", 0);
        const second = harness.field(
            "stages",
            "Steps",
            "input[type=number]",
            1,
        );
        first.focus();
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 0,
            stageIdx: 0,
        });
        second.focus();
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 0,
            stageIdx: 1,
        });
        expect(second.value).toBe("12");
    });
});
