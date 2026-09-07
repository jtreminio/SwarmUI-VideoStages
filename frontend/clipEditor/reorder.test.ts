import { afterEach, describe, expect, it } from "@jest/globals";
import { editorHarness } from "../__test_helpers__/editor";
import { getState } from "../persistence/repository";
import { getSelection } from "../selection";

describe("clip order in the production editor", () => {
    const harness = editorHarness();
    afterEach(() => {
        Reflect.deleteProperty(globalThis, "AdvancedPopover");
    });

    it("moves a clip in both directions while preserving its contents and selection", () => {
        harness.setup([
            { duration: 4, stages: [{ steps: 8 }], prompt: "Harbor" },
            { duration: 6, stages: [{ steps: 12 }], prompt: "Departure" },
            { duration: 3, stages: [{}], prompt: "At sea" },
        ]);
        const menus: { key: string; action(): void }[][] = [];
        Object.defineProperty(globalThis, "AdvancedPopover", {
            configurable: true,
            value: class {
                constructor(
                    _id: string,
                    actions: { key: string; action(): void }[],
                ) {
                    menus.push(actions);
                }
            },
        });
        const original = getState().clips;
        const move = (index: number, label: string) => {
            document
                .querySelector<HTMLButtonElement>(
                    `[data-clip-index="${index}"] [data-testid="clip-actions"]`,
                )
                ?.click();
            const action = menus.at(-1)?.find((entry) => entry.key === label);
            expect(action).toBeDefined();
            action?.action();
        };
        move(0, "Move later");
        expect(getState().clips.map((clip) => clip.id)).toEqual([
            original[1].id,
            original[0].id,
            original[2].id,
        ]);
        expect(getState().clips[1].prompt).toBe("Harbor");
        expect(getState().clips[1].stages[0].steps).toBe(8);
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 1,
            stageIdx: 0,
        });
        expect(
            document
                .querySelector('[data-clip-index="1"]')
                ?.classList.contains("selected"),
        ).toBe(true);
        move(1, "Move earlier");
        expect(getState().clips.map((clip) => clip.id)).toEqual(
            original.map((clip) => clip.id),
        );
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 0,
            stageIdx: 0,
        });
    });
});
