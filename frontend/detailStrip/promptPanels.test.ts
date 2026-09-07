import { describe, expect, it, jest } from "@jest/globals";
import { lastSavedClips } from "../__test_helpers__/dom";
import {
    commitNumber,
    committedClips,
    editorHarness,
} from "../__test_helpers__/editor";
import { setSelection } from "../selection";
import type { Clip } from "../types";

describe("production editor prompt panels", () => {
    const h = editorHarness();
    const minorRows = (): HTMLElement[] =>
        Array.from(
            h
                .section("relay-prompts")
                .querySelectorAll<HTMLElement>("[data-testid=editor-item]"),
        );
    const minorEditor = (index: number): HTMLTextAreaElement => {
        const input = h
            .item("relay-prompts", index)
            .querySelector<HTMLTextAreaElement>("textarea");
        if (!input) throw new Error(`Missing relay prompt ${index}`);
        return input;
    };

    it("edits the clip's major prompt (debounced) through saveClips", () => {
        h.setup([{ duration: 5, stages: [{}] }]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        expect(h.section("prompt").querySelector("h3")?.textContent).toBe(
            "Prompt & clip",
        );
        jest.useFakeTimers();
        const editor =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!editor) {
            throw new Error("prompt textarea missing");
        }

        editor.blur();
        editor.value = "a wide landscape";
        editor.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].prompt).toBe(
            "a wide landscape",
        );
    });

    it("uses the current global prompt as the empty clip prompt placeholder", () => {
        h.setup([{ duration: 5, stages: [{}] }]);
        const globalPrompt =
            document.querySelector<HTMLTextAreaElement>("#input_prompt");
        if (!globalPrompt) {
            throw new Error("global prompt input missing");
        }
        globalPrompt.value = "A dancer crosses a sunlit hall";

        setSelection({ kind: "prompt-major", clipIdx: 0 });

        expect(
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt")
                ?.placeholder,
        ).toBe("A dancer crosses a sunlit hall");
    });

    it("navigates to the major prompt through the section bar", () => {
        h.setup([{ duration: 5, stages: [{}], prompt: "existing text" }]);
        h.root
            .querySelector<HTMLButtonElement>("[data-testid=nav-prompt]")
            ?.click();
        expect(
            h.root
                .querySelector("[data-testid=nav-prompt]")
                ?.getAttribute("aria-current"),
        ).toBe("location");
        expect(
            h.section("prompt").querySelector<HTMLTextAreaElement>("textarea")
                ?.value,
        ).toBe("existing text");
    });

    it("adds an editable relay prompt from its section", () => {
        h.setup([{ duration: 5, stages: [{}] }]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(
                "[data-testid='add-relay-prompts']",
            )
            ?.click();
        expect(committedClips()[0].promptWindows).toHaveLength(1);
        expect(minorEditor(0).value).toBe("");
    });

    it("keeps persisted unsupported relays readable with their capability reason", () => {
        h.setup([
            {
                duration: 5,
                stages: [{ model: "removed-model.safetensors" }],
                windows: [{ start: 1, duration: 2, prompt: "dormant" }],
            },
        ]);
        expect(minorEditor(0).value).toBe("dormant");
        expect(
            h
                .section("relay-prompts")
                .querySelector<HTMLButtonElement>(
                    "[data-testid=add-relay-prompts]",
                )?.disabled,
        ).toBe(true);
    });

    it("does not steal focus / snap the caret when the major prompt is re-rendered in place", () => {
        h.setup([{ duration: 5, stages: [{}], prompt: "existing text" }]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        const before =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!before) {
            throw new Error("prompt textarea missing");
        }

        before.focus();
        before.setSelectionRange(3, 3);

        h.editor.refresh();
        const after =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!after) {
            throw new Error("prompt textarea missing after render");
        }
        expect(document.activeElement).toBe(after);
        expect(after.selectionStart).toBe(3);
        expect(after.selectionEnd).toBe(3);
    });

    it("renders every relay with its own prompt and time range", () => {
        h.setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "w0" },
                    { start: 4, duration: 2, prompt: "w1" },
                    { start: 8, duration: 2, prompt: "w2" },
                ],
            },
        ]);
        expect(minorRows()).toHaveLength(3);
        expect([0, 1, 2].map((i) => minorEditor(i).value)).toEqual([
            "w0",
            "w1",
            "w2",
        ]);
        expect(
            h
                .item("relay-prompts", 1)
                .querySelector<HTMLInputElement>(
                    'input[data-vst-focus-key$="-begin"]',
                )?.value,
        ).toBe("4");
        expect(
            h
                .item("relay-prompts", 1)
                .querySelector<HTMLInputElement>(
                    'input[data-vst-focus-key$="-end"]',
                )?.value,
        ).toBe("6");
    });

    it("keeps neighboring relay editors available while selecting one", () => {
        h.setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "w0" },
                    { start: 4, duration: 2, prompt: "w1" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 1 });
        expect(
            h.root
                .querySelector("[data-testid=nav-relay-prompts]")
                ?.getAttribute("aria-current"),
        ).toBe("location");
        expect(minorEditor(0).value).toBe("w0");
        expect(minorEditor(1).value).toBe("w1");
    });

    it("flushes one relay edit before switching to another relay", () => {
        h.setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "old0" },
                    { start: 4, duration: 2, prompt: "old1" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        jest.useFakeTimers();

        const e0 = minorEditor(0);
        e0.value = "red car";
        e0.dispatchEvent(new Event("input", { bubbles: true }));
        minorEditor(1).focus();
        h.editor.flushPending();
        expect(
            lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[0].prompt,
        ).toBe("red car");
        const e1 = minorEditor(1);
        e1.value = "blue sky";
        e1.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);

        e1.dispatchEvent(
            new FocusEvent("focusout", {
                bubbles: true,
                relatedTarget: document.body,
            }),
        );
        const windows = lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows;
        expect(windows[0].prompt).toBe("red car");
        expect(windows[1].prompt).toBe("blue sky");
        jest.useRealTimers();
    });

    it("flushes a held prompt edit before adding a stage", () => {
        h.setup([
            {
                duration: 12,
                stages: [{}],
                prompt: "old prompt",
                windows: [{ start: 1, duration: 2, prompt: "w0" }],
            },
        ]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        jest.useFakeTimers();

        const editor = document.querySelector<HTMLTextAreaElement>(
            'textarea[data-vst-focus-key$="prompt-major"]',
        );
        if (!editor) {
            throw new Error("major editor missing");
        }
        editor.focus();
        editor.value = "new prompt";
        editor.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(h.saveSpy).not.toHaveBeenCalled();

        h.root
            .querySelector<HTMLButtonElement>("[data-testid=add-stages]")
            ?.click();
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].prompt).toBe("new prompt");
        jest.useRealTimers();
    });

    it("deletes the selected relay window via its row Delete button", () => {
        h.setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "keep" },
                    { start: 4, duration: 2, prompt: "drop" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 1 });
        h.item("relay-prompts", 1)
            .querySelector<HTMLButtonElement>("[data-testid=remove-item]")
            ?.click();
        expect(committedClips()[0].promptWindows).toHaveLength(1);
        expect(committedClips()[0].promptWindows[0].prompt).toBe("keep");
        expect(minorEditor(0).value).toBe("keep");
    });

    it("edits a relay window's begin/end with clamping and refreshes the editor", () => {
        h.setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "w0" },
                    { start: 6, duration: 2, prompt: "w1" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        const beginInput = () =>
            minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key$="minor-0-begin"]',
            );
        const endInput = () =>
            minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key$="minor-0-end"]',
            );

        const end = endInput();
        if (!end) {
            throw new Error("end input missing");
        }
        commitNumber(end, "4");
        let w0 = lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[0];
        expect(w0.start).toBe(1);
        expect(w0.duration).toBe(3);

        expect(h.refreshSpy).toHaveBeenCalled();

        const begin = beginInput();
        if (!begin) {
            throw new Error("begin input missing");
        }
        commitNumber(begin, "9");
        w0 = lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[0];

        expect(w0.start).toBe(3.8);
        expect(w0.start).toBeLessThan(6);
    });

    it("bounds a relay window's begin/end inputs at its neighbours", () => {
        h.setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "w0" },
                    { start: 6, duration: 2, prompt: "w1" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });

        const w0End = minorRows()[0].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key$="minor-0-end"]',
        );
        expect(w0End?.max).toBe("6");

        minorEditor(1).focus();
        h.editor.flushPending();
        const w1Begin = minorRows()[1].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key$="minor-1-begin"]',
        );
        expect(w1Begin?.min).toBe("3");

        minorEditor(0).focus();
        const w0Begin = minorRows()[0].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key$="minor-0-begin"]',
        );
        expect(w0Begin?.min).toBe("0");
        minorEditor(1).focus();
        h.editor.flushPending();
        const w1End = minorRows()[1].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key$="minor-1-end"]',
        );
        expect(w1End?.max).toBe("12");

        expect(w0End?.min).toBe("0.3");
        expect(w0Begin?.max).toBe("11.7");
    });

    it("keeps the major prompt and relay sections available together", () => {
        h.setup([
            {
                duration: 10,
                stages: [{}],
                prompt: "base",
                windows: [{ start: 2, duration: 3, prompt: "x" }],
            },
        ]);
        expect(
            h.section("prompt").querySelector<HTMLTextAreaElement>("textarea")
                ?.value,
        ).toBe("base");
        expect(minorEditor(0).value).toBe("x");
        expect(
            h
                .section("relay-prompts")
                .querySelector("[data-testid=add-relay-prompts]")?.textContent,
        ).toBe("+ Add relay prompt");
    });
});
