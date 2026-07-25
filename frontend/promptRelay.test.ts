import { afterEach, describe, expect, it } from "@jest/globals";
import { minimalClip } from "./__test_helpers__/clipFixtures";
import { normalizePromptWindows } from "./normalization";
import { readGlobalPrompt } from "./swarmInputs";
import { computeRegionLayout } from "./timelineView/layout";
import { renderPromptTrackRow } from "./timelineView/trackRows";
import type { Clip } from "./types";

describe("normalizePromptWindows", () => {
    it("parses canonical camelCase, sorts by start, and drops non-positive durations", () => {
        const windows = normalizePromptWindows({
            promptWindows: [
                { prompt: "late", start: 3, duration: 1 },
                { prompt: "zero", start: 1, duration: 0 },
                { prompt: "early", start: 0, duration: 1 },
            ],
        });
        expect(windows).toHaveLength(2);
        expect(windows[0]).toEqual({
            prompt: "early",
            start: 0,
            duration: 1,
        });
        expect(windows[1].prompt).toBe("late");
    });

    it("returns an empty array when there is no promptWindows field", () => {
        expect(normalizePromptWindows({})).toEqual([]);
    });

    it("preserves a small stored duration without flooring it (matches the backend)", () => {
        const windows = normalizePromptWindows({
            promptWindows: [{ prompt: "brief", start: 0, duration: 0.1 }],
        });
        expect(windows).toHaveLength(1);
        expect(windows[0].duration).toBeCloseTo(0.1, 6);
    });

    it("does not treat a bare `seconds` field as a duration (backend reads only `duration`)", () => {
        expect(
            normalizePromptWindows({
                promptWindows: [{ prompt: "x", start: 0, seconds: 2 }],
            }),
        ).toEqual([]);
    });
});

describe("renderPromptTrackRow", () => {
    const build = (clips: Clip[], globalPrompt = ""): string =>
        renderPromptTrackRow(
            clips,
            computeRegionLayout(clips, { pxPerSecond: 44 }),
            44,
            globalPrompt,
        );

    it("shows the clip's own prompt as the MAJOR text", () => {
        const html = build([minimalClip({ duration: 4, prompt: "a red fox" })]);
        expect(html).toContain('data-vst-prompt="major"');
        expect(html).toContain("a red fox");
    });

    it("falls back to the global prompt when the clip has none", () => {
        const html = build([minimalClip({ duration: 4 })], "global words");
        expect(html).toContain("global words");
        expect(html).toContain("vst-major-inherited");
    });

    it("renders one minor segment per window and a disabled overlay for active windows", () => {
        const html = build([
            minimalClip({
                duration: 4,
                prompt: "major",
                promptWindows: [
                    { prompt: "loud", start: 1, duration: 1 },
                    { prompt: "", start: 2, duration: 1 },
                ],
            }),
        ]);
        expect((html.match(/data-vst-prompt="minor"/g) ?? []).length).toBe(2);
        // Only a window that carries a prompt disables the MAJOR prompt.
        expect((html.match(/vst-major-off/g) ?? []).length).toBe(1);
    });

    it("exposes a per-clip add lane", () => {
        const html = build([minimalClip({ duration: 4 })]);
        expect(html).toContain("data-vst-prompt-add");
        expect(html).toContain('data-clip-idx="0"');
    });
});

describe("readGlobalPrompt", () => {
    afterEach(() => {
        document.body.innerHTML = "";
    });

    const mountPrompt = (value: string): void => {
        const input = document.createElement("textarea");
        input.id = "input_prompt";
        input.value = value;
        document.body.appendChild(input);
    };

    it("returns only the text before the first <videoclip> tag", () => {
        mountPrompt("a cinematic shot <videoclip[0]>a red fox");
        expect(readGlobalPrompt()).toBe("a cinematic shot");
    });

    it("returns the whole prompt when there are no videoclip tags", () => {
        mountPrompt("just a prompt <lora:foo>");
        expect(readGlobalPrompt()).toBe("just a prompt <lora:foo>");
    });

    it("stops at a <videostages[...]> override tag too", () => {
        mountPrompt("lead words <videostages[width]:512>");
        expect(readGlobalPrompt()).toBe("lead words");
    });
});
