import { describe, expect, it } from "@jest/globals";
import {
    type ClipTextInput,
    extractGlobalPrompt,
    parseClipPrompts,
    serializeClipPrompts,
    tokenizePrompt,
} from "./promptSegments";

const clip = (
    prompt: string,
    windows: ClipTextInput["windows"] = [],
): ClipTextInput => ({ prompt, windows });

describe("tokenizePrompt", () => {
    it("treats text before the first boundary tag as leading prose", () => {
        const { leading, tags } = tokenizePrompt(
            "a cinematic shot <videoclip[0]>fox",
        );
        expect(leading).toBe("a cinematic shot ");
        expect(tags).toHaveLength(1);
        expect(tags[0].owned).toBe("section");
        expect(tags[0].clip).toBe(0);
    });

    it("does not break segments on non-videoclip tags (mpprompt/var/lora)", () => {
        const { tags } = tokenizePrompt(
            "<videoclip[0]>a fox <mpprompt:foo> in <var:x> snow",
        );
        expect(tags).toHaveLength(1);
        expect(tags[0].body).toBe("a fox <mpprompt:foo> in <var:x> snow");
    });

    it("classifies window ranges by their start/end seconds (floats ok)", () => {
        const { tags } = tokenizePrompt(
            "<videoclip[1]:1.5-3>rain<videoclip[1]:3-4>storm",
        );
        expect(tags[0].owned).toBe("window");
        expect(tags[0].window).toEqual({ start: 1.5, end: 3 });
        expect(tags[1].window).toEqual({ start: 3, end: 4 });
    });

    it("treats a window tag carrying a comma value as preserved (malformed)", () => {
        const { tags } = tokenizePrompt("<videoclip[0]:1-2,skip>rain");
        expect(tags[0].owned).toBeNull();
    });

    it("tolerates whitespace around the dash (mirrors backend TryParseWindow)", () => {
        const { tags } = tokenizePrompt("<videoclip[0]:0 - 5>rain");
        expect(tags[0].owned).toBe("window");
        expect(tags[0].window).toEqual({ start: 0, end: 5 });
    });

    it("tolerates whitespace padding around the whole window value", () => {
        const { tags } = tokenizePrompt("<videoclip[0]: 0-5 >rain");
        expect(tags[0].owned).toBe("window");
        expect(tags[0].window).toEqual({ start: 0, end: 5 });
    });

    it("accepts bare-decimal-point floats the backend NumberStyles.Float accepts", () => {
        // `.5` and `5.` are live relay windows in generation (double.TryParse Float accepts them),
        // so they must classify as windows here too.
        const leading = tokenizePrompt("<videoclip[0]:.5-2>rain");
        expect(leading.tags[0].owned).toBe("window");
        expect(leading.tags[0].window).toEqual({ start: 0.5, end: 2 });

        const trailing = tokenizePrompt("<videoclip[0]:5.-20>rain");
        expect(trailing.tags[0].owned).toBe("window");
        expect(trailing.tags[0].window).toEqual({ start: 5, end: 20 });
    });

    it("rejects values the backend rejects (exponent-dash, leading dash)", () => {
        // `1e-5-2`: backend splits on the first dash -> left half "1e" fails to parse.
        expect(
            tokenizePrompt("<videoclip[0]:1e-5-2>x").tags[0].owned,
        ).toBeNull();
        // `-5-2`: backend's IndexOf('-') == 0 (leading dash) -> rejected (empty/negative start).
        expect(tokenizePrompt("<videoclip[0]:-5-2>x").tags[0].owned).toBeNull();
        // Trailing garbage is not a strict float.
        expect(
            tokenizePrompt("<videoclip[0]:5px-2>x").tags[0].owned,
        ).toBeNull();
    });

    it("preserves bare, stage-scoped section, and override tags verbatim", () => {
        for (const raw of [
            "<videoclip>global text",
            "<videoclip[0,1]>stage section",
            "<videoclip[0,seed]:123>",
            "<videoclip[0,1,seed]:123>",
            "<videostages[width]:512>",
        ]) {
            const { tags } = tokenizePrompt(raw);
            expect(tags[0].owned).toBeNull();
        }
    });

    it("does not treat a multi-index ranged tag as a window (clip-level only)", () => {
        const { tags } = tokenizePrompt("<videoclip[0,1]:1-2>stage-ish");
        expect(tags[0].owned).toBeNull();
    });

    it("treats a malformed videoclip tag (no closing >) as preserved", () => {
        const { tags } = tokenizePrompt("<videoclip[0 broken");
        expect(tags[0].owned).toBeNull();
    });
});

describe("parseClipPrompts", () => {
    it("extracts section text and second-based windows per clip", () => {
        const { sections, windows } = parseClipPrompts(
            "lead <videoclip[0]>fox<videoclip[0]:1.5-4>rain<videoclip[1]>bear",
        );
        expect(sections.get(0)).toBe("fox");
        expect(sections.get(1)).toBe("bear");
        expect(windows.get(0)).toEqual([
            { start: 1.5, duration: 2.5, prompt: "rain" },
        ]);
    });
});

describe("parseClipPrompts", () => {
    it("parses a whitespace-padded window and re-serializes it canonically", () => {
        const { windows } = parseClipPrompts("<videoclip[0]:0 - 5>rain");
        expect(windows.get(0)).toEqual([
            { start: 0, duration: 5, prompt: "rain" },
        ]);
        const out = serializeClipPrompts("<videoclip[0]:0 - 5>rain", [
            clip("", [{ start: 0, duration: 5, prompt: "rain" }]),
        ]);
        expect(out).toBe("<videoclip[0]:0-5>rain");
    });

    it("parses a bare-decimal-point window and canonicalizes it to `0.5-2`", () => {
        const { windows } = parseClipPrompts("<videoclip[0]:.5-2>rain");
        expect(windows.get(0)).toEqual([
            { start: 0.5, duration: 1.5, prompt: "rain" },
        ]);
        const out = serializeClipPrompts("<videoclip[0]:.5-2>rain", [
            clip("", [{ start: 0.5, duration: 1.5, prompt: "rain" }]),
        ]);
        expect(out).toBe("<videoclip[0]:0.5-2>rain");
    });
});

describe("serializeClipPrompts", () => {
    it("authors sections and second-based windows, keeping leading prose", () => {
        const out = serializeClipPrompts("global", [
            clip("fox", [{ start: 1.5, duration: 2.5, prompt: "rain" }]),
        ]);
        expect(out).toBe("global\n<videoclip[0]>fox\n<videoclip[0]:1.5-4>rain");
    });

    it("appends a new clip section in clip-index order", () => {
        const out = serializeClipPrompts("<videoclip[0]>fox", [
            clip("fox"),
            clip("bear"),
        ]);
        expect(out).toBe("<videoclip[0]>fox\n<videoclip[1]>bear");
    });

    it("drops owned tags for clips that no longer exist (deletion renumber)", () => {
        const out = serializeClipPrompts(
            "<videoclip[0]>fox<videoclip[1]>bear",
            [clip("bear")],
        );
        expect(out).toBe("<videoclip[0]>bear");
    });

    it("omits the section tag for a clip whose prompt is blank", () => {
        const out = serializeClipPrompts("", [clip("")]);
        expect(out).toBe("");
    });

    it("preserves override / stage-scoped (comma) tags on rewrite", () => {
        const out = serializeClipPrompts(
            "<videoclip[0]>old<videoclip[0,seed]:123>",
            [clip("new")],
        );
        expect(out).toContain("<videoclip[0]>new");
        expect(out).toContain("<videoclip[0,seed]:123>");
    });
});

describe("extractGlobalPrompt", () => {
    it("returns only the text before the first videoclip tag", () => {
        expect(extractGlobalPrompt("hello <lora:x> <videoclip[0]>fox")).toBe(
            "hello <lora:x>",
        );
    });
});

describe("round-trip: serialize -> parse is symmetric", () => {
    it("survives 2 clips, an active window, an override tag, and an inline mpprompt", () => {
        const clips: ClipTextInput[] = [
            clip("a red fox <mpprompt:winter> at dawn", [
                { start: 1.5, duration: 2.5, prompt: "snow flurry" },
            ]),
            clip("a bear"),
        ];
        // Start from a prompt that already carries a preserved override tag.
        const initial = "cinematic <videoclip[0,seed]:42>";
        const serialized = serializeClipPrompts(initial, clips);

        expect(serialized).toContain("<videoclip[0]:1.5-4>snow flurry");
        // Override tag preserved verbatim.
        expect(serialized).toContain("<videoclip[0,seed]:42>");

        const { sections, windows } = parseClipPrompts(serialized);
        expect(sections.get(0)).toBe("a red fox <mpprompt:winter> at dawn");
        expect(sections.get(1)).toBe("a bear");
        expect(windows.get(0)).toEqual([
            { start: 1.5, duration: 2.5, prompt: "snow flurry" },
        ]);
        expect(windows.get(1)).toBeUndefined();

        const reparsedClips: ClipTextInput[] = [
            clip(sections.get(0) ?? "", windows.get(0) ?? []),
            clip(sections.get(1) ?? "", windows.get(1) ?? []),
        ];
        expect(serializeClipPrompts(serialized, reparsedClips)).toBe(
            serialized,
        );
    });
});
