import { describe, expect, it } from "@jest/globals";
import {
    assignMissingHues,
    BASE_HUE,
    clipHueCss,
    isAssignedHue,
    normalizeStoredHue,
    pickDistinctHue,
    UNASSIGNED_HUE,
} from "./clipColor";

describe("isAssignedHue", () => {
    it("accepts integers in [0, 359] and rejects everything else", () => {
        expect(isAssignedHue(0)).toBe(true);
        expect(isAssignedHue(359)).toBe(true);
        expect(isAssignedHue(210)).toBe(true);
        expect(isAssignedHue(UNASSIGNED_HUE)).toBe(false);
        expect(isAssignedHue(360)).toBe(false);
        expect(isAssignedHue(12.5)).toBe(false);
        expect(isAssignedHue(Number.NaN)).toBe(false);
        expect(isAssignedHue("120")).toBe(false);
        expect(isAssignedHue(null)).toBe(false);
    });
});

describe("normalizeStoredHue", () => {
    it("keeps valid hues and wraps out-of-range numbers into [0, 359]", () => {
        expect(normalizeStoredHue(120)).toBe(120);
        expect(normalizeStoredHue(0)).toBe(0);
        expect(normalizeStoredHue(360)).toBe(0);
        expect(normalizeStoredHue(400)).toBe(40);
        expect(normalizeStoredHue(-30)).toBe(330);
        expect(normalizeStoredHue("200")).toBe(200);
        expect(normalizeStoredHue(210.4)).toBe(210);
    });

    it("returns the unassigned sentinel for missing/unparseable values", () => {
        expect(normalizeStoredHue(undefined)).toBe(UNASSIGNED_HUE);
        expect(normalizeStoredHue(null)).toBe(UNASSIGNED_HUE);
        expect(normalizeStoredHue("")).toBe(UNASSIGNED_HUE);
        expect(normalizeStoredHue("nope")).toBe(UNASSIGNED_HUE);
        expect(normalizeStoredHue(Number.NaN)).toBe(UNASSIGNED_HUE);
    });
});

describe("pickDistinctHue", () => {
    it("returns the base hue when nothing is assigned yet", () => {
        expect(pickDistinctHue([])).toBe(BASE_HUE);
        expect(pickDistinctHue([UNASSIGNED_HUE])).toBe(BASE_HUE);
    });

    it("picks the hue farthest from a single existing hue (opposite side)", () => {
        expect(pickDistinctHue([0])).toBe(180);
        expect(pickDistinctHue([210])).toBe(30);
    });

    it("maximizes the minimum distance to all existing hues", () => {
        // Largest gap on the wheel is 210 -> 30 (wrapping, 180 wide); its
        // midpoint is 300.
        expect(pickDistinctHue([30, 120, 210])).toBe(300);
    });

    it("ignores unassigned sentinels among the existing hues", () => {
        expect(pickDistinctHue([UNASSIGNED_HUE, 0, UNASSIGNED_HUE])).toBe(180);
    });
});

describe("assignMissingHues", () => {
    it("leaves already-assigned hues untouched", () => {
        const clips = [{ hue: 40 }, { hue: 250 }];
        assignMissingHues(clips);
        expect(clips.map((c) => c.hue)).toEqual([40, 250]);
    });

    it("fills sentinels with distinct hues spread from what's assigned", () => {
        const clips = [
            { hue: UNASSIGNED_HUE },
            { hue: UNASSIGNED_HUE },
            { hue: UNASSIGNED_HUE },
        ];
        assignMissingHues(clips);
        const hues = clips.map((c) => c.hue);
        for (const hue of hues) {
            expect(isAssignedHue(hue)).toBe(true);
        }
        expect(new Set(hues).size).toBe(hues.length);
        expect(hues[0]).toBe(BASE_HUE);
    });

    it("is deterministic and stable across repeated runs (reorder-safe)", () => {
        const first = [{ hue: UNASSIGNED_HUE }, { hue: UNASSIGNED_HUE }];
        const second = [{ hue: UNASSIGNED_HUE }, { hue: UNASSIGNED_HUE }];
        assignMissingHues(first);
        assignMissingHues(second);
        expect(first.map((c) => c.hue)).toEqual(second.map((c) => c.hue));
        // Re-running is a no-op once assigned.
        const snapshot = first.map((c) => c.hue);
        assignMissingHues(first);
        expect(first.map((c) => c.hue)).toEqual(snapshot);
    });

    it("only fills the gaps when some clips already have a hue", () => {
        const clips = [{ hue: 100 }, { hue: UNASSIGNED_HUE }, { hue: 100 }];
        assignMissingHues(clips);
        expect(clips[0].hue).toBe(100);
        expect(clips[2].hue).toBe(100);
        expect(clips[1].hue).toBe(280); // opposite the assigned 100
    });
});

describe("clipHueCss", () => {
    it("renders a theme-neutral hsl() the color-mix pipeline consumes", () => {
        expect(clipHueCss(210)).toBe("hsl(210 65% 55%)");
        expect(clipHueCss(0)).toBe("hsl(0 65% 55%)");
    });

    it("falls back to the base hue for an unassigned value", () => {
        expect(clipHueCss(UNASSIGNED_HUE)).toBe(`hsl(${BASE_HUE} 65% 55%)`);
    });
});
