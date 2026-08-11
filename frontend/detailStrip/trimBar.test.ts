import { describe, expect, it } from "@jest/globals";

import type { SourceRange } from "../trimGeometry";
import { buildTrimBar, pointerSecondsAt, trimBarGeometry } from "./trimBar";

const LIMITS = { limitSeconds: 12.4, minLengthSeconds: 1, fps: 30 };

const build = (
    range: SourceRange = { startSeconds: 2.1, lengthSeconds: 4.2 },
): {
    element: HTMLElement;
    changes: SourceRange[];
    sync: (next: SourceRange) => void;
} => {
    const changes: SourceRange[] = [];
    const bar = buildTrimBar({
        range,
        limits: LIMITS,
        onChange: (next) => changes.push(next),
    });
    return { element: bar.element, changes, sync: bar.sync };
};

const gripOf = (element: HTMLElement, edge: "in" | "out"): HTMLElement => {
    const grip = element.querySelector<HTMLElement>(
        `[data-vst-trim-grip="${edge}"]`,
    );
    if (!grip) {
        throw new Error(`no ${edge} grip`);
    }
    return grip;
};

const windowOf = (element: HTMLElement): HTMLElement => {
    const window = element.querySelector<HTMLElement>(".vst-trim-window");
    if (!window) {
        throw new Error("trim window missing");
    }
    return window;
};

const press = (element: HTMLElement, key: string, shift = false): void => {
    element.dispatchEvent(
        new KeyboardEvent("keydown", { key, shiftKey: shift, bubbles: true }),
    );
};

describe("trim bar geometry", () => {
    it("maps a pointer position onto source seconds", () => {
        expect(pointerSecondsAt(150, 100, 200, 12.4)).toBeCloseTo(3.1, 5);
    });

    it("clamps a pointer dragged past either end of the track", () => {
        expect(pointerSecondsAt(40, 100, 200, 12.4)).toBe(0);
        expect(pointerSecondsAt(900, 100, 200, 12.4)).toBe(12.4);
    });

    /** jsdom reports a zero-width rect, which must not divide by zero. */
    it("reports zero for an unmeasurable track", () => {
        expect(pointerSecondsAt(150, 0, 0, 12.4)).toBe(0);
    });

    it("places the window over the kept part of the source", () => {
        expect(
            trimBarGeometry({ startSeconds: 3.1, lengthSeconds: 6.2 }, 12.4),
        ).toEqual({ leftPct: 25, widthPct: 50 });
    });
});

describe("trim bar", () => {
    it("nudges the in point without moving the out point", () => {
        const bar = build();
        press(gripOf(bar.element, "in"), "ArrowRight");

        expect(bar.changes).toEqual([
            { startSeconds: 2.2, lengthSeconds: 4.1 },
        ]);
    });

    it("nudges the out point without moving the in point", () => {
        const bar = build();
        press(gripOf(bar.element, "out"), "ArrowLeft");

        expect(bar.changes).toEqual([
            { startSeconds: 2.1, lengthSeconds: 4.1 },
        ]);
    });

    it("takes a coarse step when shift is held", () => {
        const bar = build();
        press(gripOf(bar.element, "in"), "ArrowRight", true);

        expect(bar.changes[0]).toEqual({
            startSeconds: 3.1,
            lengthSeconds: 3.2,
        });
    });

    it("sends the in point home and the out point to the end of the source", () => {
        const bar = build();
        press(gripOf(bar.element, "in"), "Home");
        press(gripOf(bar.element, "out"), "End");

        expect(bar.changes).toEqual([
            { startSeconds: 0, lengthSeconds: 6.3 },
            { startSeconds: 0, lengthSeconds: 12.4 },
        ]);
    });

    /**
     * The window keyboard path is the slide, not a trim — it is the only way a
     * keyboard user reaches the behavior the old "Start (s)" field had.
     */
    it("slides the whole range from the window without changing its length", () => {
        const bar = build();
        press(windowOf(bar.element), "ArrowRight", true);

        expect(bar.changes[0]).toEqual({
            startSeconds: 3.1,
            lengthSeconds: 4.2,
        });
    });

    it("describes both limits to a screen reader as it moves", () => {
        const bar = build();
        press(gripOf(bar.element, "in"), "ArrowRight");

        expect(gripOf(bar.element, "in").getAttribute("aria-valuetext")).toBe(
            "In point, 2.2 seconds",
        );
        expect(gripOf(bar.element, "out").getAttribute("aria-valuenow")).toBe(
            "6.3",
        );
    });

    it("repaints when the document changes underneath it", () => {
        const bar = build();
        bar.sync({ startSeconds: 0, lengthSeconds: 12.4 });

        const window_ = windowOf(bar.element);
        expect(window_.style.left).toBe("0%");
        expect(window_.style.width).toBe("100%");
        expect(bar.changes).toEqual([]);
    });

    it("ignores a key it does not handle", () => {
        const bar = build();
        press(gripOf(bar.element, "in"), "a");

        expect(bar.changes).toEqual([]);
    });
});
