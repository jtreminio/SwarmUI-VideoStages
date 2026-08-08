import { describe, expect, it, jest } from "@jest/globals";
import {
    committedClips,
    crumbText,
    detail,
    detailBody,
    detailStripHarness,
    minorEditor,
    minorRows,
} from "../__test_helpers__/detailStrip";
import { lastSavedClips } from "../__test_helpers__/dom";
import { getSelection, setSelection } from "../selection";
import type { Clip } from "../types";

describe("detail strip prompt panels", () => {
    const h = detailStripHarness();
    const { setup, renderStrip } = h;

    it("edits the clip's major prompt (debounced) through saveClips", () => {
        setup([{ duration: 5, stages: [{}] }]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        expect(crumbText()).toBe("Prompts · Clip 0");
        jest.useFakeTimers();
        const editor =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!editor) {
            throw new Error("prompt textarea missing");
        }
        // The panel auto-focuses the textarea, so typing is HELD (no timer)
        // while the caret is in it. Blurring out of the dock hands the field
        // back to the debounce timer, exercising the coalesced-write path.
        editor.blur();
        editor.value = "a wide landscape";
        editor.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].prompt).toBe(
            "a wide landscape",
        );
    });

    it("auto-focuses the major prompt textarea (caret at end) on a timeline-origin selection", () => {
        setup([{ duration: 5, stages: [{}], prompt: "existing text" }]);
        // A timeline click selects the major prompt while focus is OUTSIDE the
        // dock (nothing in the dock is focused yet).
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        const editor =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!editor) {
            throw new Error("prompt textarea missing");
        }
        expect(document.activeElement).toBe(editor);
        expect(editor.selectionStart).toBe(editor.value.length);
        expect(editor.selectionEnd).toBe(editor.value.length);
    });

    it("adds and selects a relay prompt from the combined prompt sidebar", () => {
        setup([{ duration: 5, stages: [{}] }]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-add-relay")
            ?.click();
        expect(committedClips()[0].promptWindows).toHaveLength(1);
        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 0,
        });
        expect(minorEditor(0)).not.toBeNull();
    });

    it("does not steal focus / snap the caret when the major prompt is re-rendered in place", () => {
        setup([{ duration: 5, stages: [{}], prompt: "existing text" }]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        const before =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!before) {
            throw new Error("prompt textarea missing");
        }
        // User places the caret mid-text (selection change now originates from
        // inside the dock).
        before.focus();
        before.setSelectionRange(3, 3);
        // A self-triggered re-render must preserve the caret, not snap it back
        // to the end via auto-focus.
        renderStrip();
        const after =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!after) {
            throw new Error("prompt textarea missing after render");
        }
        expect(document.activeElement).toBe(after);
        expect(after.selectionStart).toBe(3);
        expect(after.selectionEnd).toBe(3);
    });

    it("lists every relay in a rail and renders only the selected editor", () => {
        setup([
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
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 1 });

        const rows = minorRows();
        expect(rows).toHaveLength(1);
        expect(rows[0].dataset.vstMinorWindow).toBe("1");
        expect(document.querySelectorAll(".vst-relay-tab")).toHaveLength(3);
        const beginEnd = (row: HTMLElement): [string, string] => [
            row.querySelector<HTMLInputElement>(
                'input[data-vst-focus-key$="-begin"]',
            )?.value ?? "",
            row.querySelector<HTMLInputElement>(
                'input[data-vst-focus-key$="-end"]',
            )?.value ?? "",
        ];
        expect(beginEnd(rows[0])).toEqual(["4", "6"]);
        expect(rows[0].querySelector("textarea")).not.toBeNull();
        expect(
            document
                .querySelectorAll(".vst-relay-tab")[1]
                .getAttribute("aria-pressed"),
        ).toBe("true");
        expect(document.activeElement).toBe(minorEditor(1));
    });

    it("switches the active relay editor from the relay rail", () => {
        setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "w0" },
                    { start: 4, duration: 2, prompt: "w1" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 0,
        });

        const before = minorEditor(0);
        document
            .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
            ?.click();
        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 1,
        });
        expect(minorEditor(1)).not.toBe(before);
        expect(document.activeElement).toBe(minorEditor(1));
        expect(minorRows()[0].dataset.vstMinorWindow).toBe("1");
    });

    it("flushes one relay edit before switching to another relay", () => {
        setup([
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
        document
            .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
            ?.click();
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

    it("flushes a held prompt edit on a press outside the dock (timeline click)", () => {
        const body = setup([
            {
                duration: 12,
                stages: [{}],
                prompt: "old prompt",
                windows: [{ start: 1, duration: 2, prompt: "w0" }],
            },
        ]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        jest.useFakeTimers();

        // Typing in the focused major editor is HELD past the debounce window.
        const editor = document.querySelector<HTMLTextAreaElement>(
            'textarea[data-vst-focus-key="prompt-major"]',
        );
        if (!editor) {
            throw new Error("major editor missing");
        }
        editor.value = "new prompt";
        editor.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(h.saveSpy).not.toHaveBeenCalled();

        // A press on the timeline never blurs the editor (track gestures
        // preventDefault their mousedown), but its concluding click can commit
        // a structural save that would stale-drop the held edit. The
        // document-level pointerdown must flush FIRST.
        body.dispatchEvent(new Event("pointerdown", { bubbles: true }));
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].prompt).toBe("new prompt");
        jest.useRealTimers();
    });

    it("deletes the active relay window via the rail Delete button", () => {
        setup([
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
        document
            .querySelector<HTMLElement>(
                '.vst-relay-tab[aria-pressed="true"] .vst-detail-delete-relay',
            )
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(committedClips()[0].promptWindows).toHaveLength(1);
        expect(committedClips()[0].promptWindows[0].prompt).toBe("keep");
        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 0,
        });
    });

    it("edits a relay window's begin/end with clamping and repaints the timeline", () => {
        setup([
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
                'input[data-vst-focus-key="minor-0-begin"]',
            );
        const endInput = () =>
            minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-0-end"]',
            );
        // A `change` while the number field is focused (spinner / Enter) commits
        // the held edit live.
        const commitNumber = (input: HTMLInputElement, value: string): void => {
            input.focus();
            input.value = value;
            input.dispatchEvent(new Event("input", { bubbles: true }));
            input.dispatchEvent(new Event("change", { bubbles: true }));
        };

        // Move the end out to 4s: end held-fixed rule keeps start=1, duration=3.
        const end = endInput();
        if (!end) {
            throw new Error("end input missing");
        }
        commitNumber(end, "4");
        let w0 = lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[0];
        expect(w0.start).toBe(1);
        expect(w0.duration).toBe(3);
        // The window edit committed through the store — the notification that
        // repaints the timeline (and the on-track relay segment) in prod.
        expect(h.refreshSpy).toHaveBeenCalled();

        // Push begin past the neighbouring window (start 6): clamped so it can't
        // cross it — begin can't exceed end - min-duration.
        const begin = beginInput();
        if (!begin) {
            throw new Error("begin input missing");
        }
        commitNumber(begin, "9");
        w0 = lastSavedClips<Clip[]>(h.saveSpy)[0].promptWindows[0];
        // begin can't reach 9: clamped to end - PROMPT_WINDOW_MIN_DURATION
        // (4 - 0.25 = 3.75, rounded to 0.1s like the timeline gesture → 3.8) and
        // never crosses the neighbouring window at start 6.
        expect(w0.start).toBe(3.8);
        expect(w0.start).toBeLessThan(6);
    });

    it("bounds a relay window's begin/end inputs at its neighbours", () => {
        setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "w0" }, // [1,3]
                    { start: 6, duration: 2, prompt: "w1" }, // [6,8]
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        // w0's END spinner stops at w1's start (6): its max attr IS the wall.
        const w0End = minorRows()[0].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="minor-0-end"]',
        );
        expect(w0End?.max).toBe("6");
        // Switch to w1: its BEGIN spinner stops at w0's end (3).
        document
            .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
            ?.click();
        const w1Begin = minorRows()[0].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="minor-1-begin"]',
        );
        expect(w1Begin?.min).toBe("3");
        // Outer edges stay clip-bounded.
        document
            .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[0]
            ?.click();
        const w0Begin = minorRows()[0].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="minor-0-begin"]',
        );
        expect(w0Begin?.min).toBe("0");
        document
            .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
            ?.click();
        const w1End = minorRows()[0].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="minor-1-end"]',
        );
        expect(w1End?.max).toBe("12");
        // The attrs sit ON the 0.1 spinner grid: a 0.25-anchored min would put
        // whole-tenth values off-grid, and the browser's down-spin snap (x.95)
        // rounds half-up straight back — END could never decrease.
        expect(w0End?.min).toBe("0.3");
        expect(w0Begin?.max).toBe("11.7"); // floor(12 - 0.25) onto the grid
    });

    it("renders major and relay prompts in one sidebar", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                windows: [{ start: 2, duration: 3, prompt: "x" }],
            },
        ]);

        setSelection({ kind: "prompt-major", clipIdx: 0 });
        const major = detail()?.querySelector<HTMLElement>(
            ".vst-detail-prompt-major",
        );
        const relayHead = detail()?.querySelector<HTMLElement>(
            '[data-vst-repeater-key="relay-prompts"]',
        );
        expect(major).not.toBeNull();
        expect(relayHead).not.toBeNull();
        expect(relayHead?.classList.contains("vst-detail-relay-section")).toBe(
            true,
        );
        expect(
            detailBody()?.querySelector(
                ".vst-detail-repeating-group .input-group-header",
            ),
        ).not.toBeNull();
        expect(
            relayHead?.querySelector(
                ':scope > .input-group-content > [data-vst-repeater-item="0"] .header-label',
            )?.textContent,
        ).toBe("R0");
        expect(crumbText()).toBe("Prompts · Clip 0");
    });
});
