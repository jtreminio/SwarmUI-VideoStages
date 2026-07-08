import { describe, expect, it } from "@jest/globals";
import {
    audioSourceBadge,
    chooseRulerStepSeconds,
    computeRulerTicks,
    escapeHtml,
    formatRulerLabel,
    formatTimeLabel,
    keyframeLeftPercent,
    keyframeTimeSeconds,
    refSourceLabel,
    refSourceShortLabel,
    safeFps,
    shortModelName,
    truncate,
} from "./timelineDetail";
import type { Stage } from "./types";

describe("keyframeTimeSeconds", () => {
    it("positions a first-frame ref from the clip start", () => {
        // 24 frames @ 24fps = 1s
        expect(keyframeTimeSeconds(24, false, 5, 24)).toBe(1);
    });

    it("positions a fromEnd ref measured back from the clip end", () => {
        // 24 frames @ 24fps = 1s; from end of 5s clip => 4s
        expect(keyframeTimeSeconds(24, true, 5, 24)).toBe(4);
    });

    it("clamps to [0, duration] when the offset exceeds the clip", () => {
        expect(keyframeTimeSeconds(1000, false, 5, 24)).toBe(5);
        expect(keyframeTimeSeconds(1000, true, 5, 24)).toBe(0);
    });

    it("falls back to 24fps when fps is missing/invalid", () => {
        expect(keyframeTimeSeconds(24, false, 5, 0)).toBe(1);
        expect(keyframeTimeSeconds(24, false, 5, Number.NaN)).toBe(1);
    });

    it("treats a zero-duration clip as a single point", () => {
        expect(keyframeTimeSeconds(24, false, 0, 24)).toBe(0);
    });

    it("floors a negative or NaN frame to the clip start", () => {
        expect(keyframeTimeSeconds(-5, false, 5, 24)).toBe(0);
        expect(keyframeTimeSeconds(Number.NaN, false, 5, 24)).toBe(0);
    });

    it("uses the 24fps default when fps is undefined", () => {
        expect(keyframeTimeSeconds(24, false, 5, undefined)).toBe(1);
    });
});

describe("keyframeLeftPercent", () => {
    it("returns 0 for a zero/invalid duration", () => {
        expect(keyframeLeftPercent(2, 0)).toBe(0);
        expect(keyframeLeftPercent(2, Number.NaN)).toBe(0);
    });

    it("clamps to 100 when time exceeds duration", () => {
        expect(keyframeLeftPercent(10, 5)).toBe(100);
    });

    it("is proportional within the region", () => {
        expect(keyframeLeftPercent(2.5, 5)).toBe(50);
        expect(keyframeLeftPercent(0, 5)).toBe(0);
    });
});

describe("ruler ticks", () => {
    it("chooseRulerStepSeconds grows the step as you zoom out", () => {
        // 200px/s -> 0.5s clears 60px spacing; 10px/s needs a 10s step.
        expect(chooseRulerStepSeconds(200)).toBe(0.5);
        expect(chooseRulerStepSeconds(44)).toBe(2);
        expect(chooseRulerStepSeconds(10)).toBe(10);
    });

    it("computeRulerTicks lays evenly spaced ticks up to total", () => {
        const ticks = computeRulerTicks(4, 44); // 44px/s -> 2s step
        expect(ticks.map((t) => t.seconds)).toEqual([0, 2, 4]);
        expect(ticks.map((t) => t.x)).toEqual([0, 88, 176]);
    });

    it("computeRulerTicks degenerates safely", () => {
        expect(computeRulerTicks(0, 44)).toEqual([{ x: 0, seconds: 0 }]);
        expect(computeRulerTicks(10, 0)).toEqual([{ x: 0, seconds: 0 }]);
    });

    it("formatRulerLabel switches to M:SS timecode at a minute", () => {
        expect(formatRulerLabel(30, "seconds", 24)).toBe("30s");
        expect(formatRulerLabel(90, "seconds", 24)).toBe("1:30");
        expect(formatRulerLabel(605, "seconds", 24)).toBe("10:05");
        expect(formatRulerLabel(2, "frames", 24)).toBe("48f");
    });
});

describe("formatTimeLabel", () => {
    it("formats seconds with at most one decimal", () => {
        expect(formatTimeLabel(2, "seconds", 24)).toBe("2s");
        expect(formatTimeLabel(1.5, "seconds", 24)).toBe("1.5s");
        expect(formatTimeLabel(1.53, "seconds", 24)).toBe("1.5s");
    });

    it("formats frames as round(seconds*fps)", () => {
        expect(formatTimeLabel(1.5, "frames", 24)).toBe("36f");
        expect(formatTimeLabel(2, "frames", 30)).toBe("60f");
    });

    it("uses the 24fps fallback for frames when fps is invalid", () => {
        expect(formatTimeLabel(1, "frames", 0)).toBe("24f");
    });
});

describe("safeFps", () => {
    it("returns the fps when positive, else 24", () => {
        expect(safeFps(30)).toBe(30);
        expect(safeFps(0)).toBe(24);
        expect(safeFps(-5)).toBe(24);
        expect(safeFps(null)).toBe(24);
        expect(safeFps(Number.NaN)).toBe(24);
    });
});

describe("escapeHtml", () => {
    it("escapes the dangerous characters", () => {
        expect(escapeHtml(`<b> & "x"`)).toBe("&lt;b&gt; &amp; &quot;x&quot;");
    });
});

describe("truncate", () => {
    it("leaves short strings intact", () => {
        expect(truncate("hello", 10)).toBe("hello");
    });

    it("adds an ellipsis when over the limit", () => {
        expect(truncate("abcdef", 4)).toBe("abc…");
    });
});

describe("refSourceLabel", () => {
    it("falls back to the buildDefaultRef default (Refiner) when blank", () => {
        expect(refSourceLabel("")).toBe("Refiner");
        expect(refSourceLabel("   ")).toBe("Refiner");
    });

    it("humanizes a Base2Edit edit{N} source", () => {
        expect(refSourceLabel("edit3")).toBe("Base2Edit Edit 3");
    });

    it("passes through known sources unchanged", () => {
        expect(refSourceLabel("Base")).toBe("Base");
        expect(refSourceLabel("Upload")).toBe("Upload");
    });
});

describe("refSourceShortLabel", () => {
    it("maps the known sources to compact codes", () => {
        expect(refSourceShortLabel("Base")).toBe("B");
        expect(refSourceShortLabel("Refiner")).toBe("R");
        expect(refSourceShortLabel("Upload")).toBe("U");
    });

    it("defaults blank/empty to the Refiner code", () => {
        expect(refSourceShortLabel("")).toBe("R");
        expect(refSourceShortLabel("   ")).toBe("R");
    });

    it("codes a Base2Edit edit{N} source as E{N}", () => {
        expect(refSourceShortLabel("edit0")).toBe("E0");
        expect(refSourceShortLabel("edit12")).toBe("E12");
    });

    it("falls back to an upper-cased prefix for an unrecognized source", () => {
        expect(refSourceShortLabel("weird")).toBe("WEI");
    });
});

describe("audioSourceBadge", () => {
    it("labels Native (incl. empty) as Native", () => {
        expect(audioSourceBadge("").label).toBe("Native");
        expect(audioSourceBadge("Native").label).toBe("Native");
    });

    it("passes through Upload / ControlNet", () => {
        expect(audioSourceBadge("Upload").label).toBe("Upload");
        expect(audioSourceBadge("ControlNet").label).toBe("ControlNet");
    });
});

describe("shortModelName", () => {
    it("strips directories and known extensions", () => {
        expect(shortModelName("a/b/wan2.1.safetensors")).toBe("wan2.1");
        expect(shortModelName("model.ckpt")).toBe("model");
    });

    it("returns (default) for empty", () => {
        expect(shortModelName("")).toBe("(default)");
    });
});

describe("stage chip helpers indirectly cover Stage typing", () => {
    it("shortModelName handles a Stage.model", () => {
        const stage = { model: "dir/foo.safetensors" } as Stage;
        expect(shortModelName(stage.model)).toBe("foo");
    });
});
