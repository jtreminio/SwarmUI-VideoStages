import { describe, expect, it } from "@jest/globals";
import { RETAKE_SOURCE } from "../__test_helpers__/authoringFixtures";
import { editorHarness } from "../__test_helpers__/editor";
import { getState } from "../persistence/repository";
import { getSelection } from "../selection";

describe("clip timing in the production editor", () => {
    const h = editorHarness();
    const edit = (
        section: string,
        label: string,
        value: string,
        index?: number,
    ) => {
        const input = h.field<HTMLInputElement>(
            section,
            label,
            "input:not([type=range])",
            index,
        );
        input.value = value;
        input.dispatchEvent(new Event("input", { bubbles: true }));
        input.dispatchEvent(new Event("change", { bubbles: true }));
        h.editor.flushPending();
    };

    it("changes the selected clip duration while preserving other clips and its keyframes", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}],
                frameRefs: [{ source: "Base", frame: 1 }],
            },
            { duration: 7, stages: [{}] },
        ]);
        edit("prompt", "Duration (s)", "3.5");
        expect(getState().clips.map((clip) => clip.duration)).toEqual([3.5, 7]);
        expect(getState().clips[0].frameRefs[0].frame).toBe(1);
    });

    it("edits relay begin and end within neighbouring windows and preserves their prompts", () => {
        h.setup([
            {
                duration: 10,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "First" },
                    { start: 5, duration: 2, prompt: "Second" },
                ],
            },
        ]);
        edit("relay-prompts", "Begin (s)", "2", 0);
        edit("relay-prompts", "End (s)", "9", 0);
        expect(getState().clips[0].promptWindows).toMatchObject([
            { start: 2, duration: 3, prompt: "First" },
            { start: 5, duration: 2, prompt: "Second" },
        ]);
    });

    it("retimes a keyframe and keeps each stage's strength associated with it", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}, {}],
                frameRefs: [
                    { source: "Base", frame: 1 },
                    { source: "Base", frame: 25 },
                ],
            },
        ]);
        const before = getState().clips[0].stages.map((stage) => [
            ...stage.frameRefStrengths,
        ]);
        edit("keyframes", "Attach at Frame", "61", 0);
        expect(getState().clips[0].frameRefs.map((ref) => ref.frame)).toEqual([
            61, 25,
        ]);
        expect(
            getState().clips[0].stages.map((stage) => stage.frameRefStrengths),
        ).toEqual(before);
    });

    it("adds and removes keyframes with aligned per-stage strengths and an editable selection", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}, {}],
                frameRefs: [{ source: "Base", frame: 25 }],
            },
        ]);
        const first = getState().clips[0].frameRefs[0].id;
        h.section("keyframes")
            .querySelector<HTMLButtonElement>('[data-action="add"]')
            ?.click();
        expect(getState().clips[0].frameRefs).toHaveLength(2);
        expect(
            getState().clips[0].stages.map(
                (stage) => stage.frameRefStrengths.length,
            ),
        ).toEqual([2, 2]);
        expect(getSelection()).toMatchObject({
            kind: "ref",
            clipIdx: 0,
            refIdx: 1,
        });
        h.item("keyframes", 1)
            .querySelector<HTMLButtonElement>('[data-action="delete"]')
            ?.click();
        expect(getState().clips[0].frameRefs.map((ref) => ref.id)).toEqual([
            first,
        ]);
        expect(
            getState().clips[0].stages.map(
                (stage) => stage.frameRefStrengths.length,
            ),
        ).toEqual([1, 1]);
        expect(getSelection()).toMatchObject({
            kind: "ref",
            clipIdx: 0,
            refIdx: 0,
        });
    });

    it("moves and resizes the retake while keeping its range inside the clip", () => {
        h.setup([
            {
                duration: 10,
                stages: [{}],
                initVideo: RETAKE_SOURCE,
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 0.6 },
            },
        ]);
        edit("retake", "Start (s)", "4");
        expect(getState().clips[0].retake).toMatchObject({
            startSeconds: 4,
            lengthSeconds: 3,
            strength: 0.6,
        });
        edit("retake", "Length (s)", "9");
        expect(getState().clips[0].retake).toMatchObject({
            startSeconds: 4,
            lengthSeconds: 6,
            strength: 0.6,
        });
    });

    it("creates a retake and makes the section ready for a new window after deletion", () => {
        h.setup([{ duration: 10, stages: [{}], initVideo: RETAKE_SOURCE }]);
        const add = () =>
            h
                .section("retake")
                .querySelector<HTMLButtonElement>(".vst-detail-add-retake")
                ?.click();
        add();
        expect(getState().clips[0].retake).toMatchObject({
            startSeconds: 0,
            lengthSeconds: 3,
            strength: 1,
        });
        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
        const original = getState().clips[0].retake?.id;
        h.section("retake")
            .querySelector<HTMLButtonElement>('[data-action="delete"]')
            ?.click();
        expect(
            h
                .section("retake")
                .querySelector<HTMLButtonElement>(".vst-detail-add-retake")
                ?.disabled,
        ).toBe(false);
        add();
        expect(getState().clips[0].retake).toMatchObject({
            startSeconds: 0,
            lengthSeconds: 3,
            strength: 1,
        });
        expect(getState().clips[0].retake?.id === original).toBe(false);
    });
});
