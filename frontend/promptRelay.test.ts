import { afterEach, describe, expect, it } from "@jest/globals";
import { normalizePromptWindows } from "./normalizationMedia";
import { readGlobalPrompt } from "./swarmInputs";

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
