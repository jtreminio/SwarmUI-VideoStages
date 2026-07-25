// Tokenizer / serializer for the `<videoclip...>` prompt grammar.
//
// The positive-prompt textarea carries plain user prose plus tags that the
// VideoStages timeline owns:
//   <videoclip[N]>            — clip N's main prompt (text follows until the next boundary)
//   <videoclip[N]:A-B>        — a prompt window on clip N; A,B are SECONDS (floats ok, 0<=A<B)
//
// Everything else that starts a boundary tag is PRESERVED verbatim and never
// authored by the UI:
//   <videoclip>               — bare, global-for-all-clips text
//   <videoclip[N,M]>          — stage-scoped section
//   <videoclip[N,field]:v>    — clip override (trailing non-numeric token = field)
//   <videoclip[N,M,field]:v>  — stage override
//   <videostages[field]:v>    — root override
//
// Windows are clip-level ONLY. A ranged tag with more than one index
// (`<videoclip[N,M]:A-B>`) is not valid syntax; the segmenter simply leaves it
// non-owned / preserved-verbatim.
//
// Indices are comma-separated inside ONE bracket group. Classification: split
// the bracket content on ','. A single all-numeric token with NO value is an
// owned section; a single all-numeric token with an `A-B` seconds range value
// is an owned window; anything else with a `<videoclip` prefix is preserved. A
// comma inside a window VALUE makes the tag preserved.
//
// Only `<videoclip`-prefixed and `<videostages[`-prefixed tag STARTS are
// segment boundaries. Any other `<...>` (e.g. `<mpprompt:...>`, `<lora:...>`,
// `<var:...>`) is prose belonging to the current segment.

export interface ClipTextInput {
    prompt: string;
    windows: { start: number; duration: number; prompt: string }[];
}

export interface ParsedWindow {
    start: number;
    duration: number;
    prompt: string;
}

export interface ParsedClipPrompts {
    sections: Map<number, string>;
    windows: Map<number, ParsedWindow[]>;
}

interface TagSegment {
    tagRaw: string;
    body: string;
    owned: "section" | "window" | null;
    clip: number | null;
    window: { start: number; end: number } | null;
}

interface Tokenized {
    leading: string;
    tags: TagSegment[];
}

const BOUNDARY_RE = /<videoclip(?=[>[])|<videostages\[/gi;
const TAG_RE = /^<videoclip(?:\[([^\]]*)\])?(?::([^>]*))?>$/i;
const INDEX_RE = /^\d+$/;
const FLOAT_RE = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/;

const preserved: Pick<TagSegment, "owned" | "clip" | "window"> = {
    owned: null,
    clip: null,
    window: null,
};

const parseWindowValue = (
    value: string,
): { start: number; end: number } | null => {
    const trimmed = value.trim();
    if (trimmed.length === 0 || trimmed.includes(",")) {
        return null;
    }
    const dash = trimmed.indexOf("-");
    if (dash <= 0 || dash >= trimmed.length - 1) {
        return null;
    }
    const left = trimmed.slice(0, dash).trim();
    const right = trimmed.slice(dash + 1).trim();
    if (!FLOAT_RE.test(left) || !FLOAT_RE.test(right)) {
        return null;
    }
    const start = Math.max(0, Number.parseFloat(left));
    const end = Number.parseFloat(right);
    if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) {
        return null;
    }
    return { start, end };
};

const classify = (
    tagRaw: string,
): Pick<TagSegment, "owned" | "clip" | "window"> => {
    const match = TAG_RE.exec(tagRaw);
    if (!match) {
        return preserved;
    }
    const bracket = match[1];
    const value = match[2];
    if (bracket === undefined) {
        return preserved;
    }
    const tokens = bracket.split(",").map((token) => token.trim());
    if (tokens.length !== 1 || !INDEX_RE.test(tokens[0])) {
        return preserved;
    }
    const clip = Number.parseInt(tokens[0], 10);
    if (value === undefined) {
        return { owned: "section", clip, window: null };
    }
    const window = parseWindowValue(value);
    if (window) {
        return { owned: "window", clip, window };
    }
    return preserved;
};

export const tokenizePrompt = (prompt: string): Tokenized => {
    const text = prompt ?? "";
    BOUNDARY_RE.lastIndex = 0;
    const starts: number[] = [];
    for (
        let match = BOUNDARY_RE.exec(text);
        match !== null;
        match = BOUNDARY_RE.exec(text)
    ) {
        starts.push(match.index);
    }
    if (starts.length === 0) {
        return { leading: text, tags: [] };
    }
    const leading = text.slice(0, starts[0]);
    const tags: TagSegment[] = [];
    for (let i = 0; i < starts.length; i++) {
        const start = starts[i];
        const nextStart = i + 1 < starts.length ? starts[i + 1] : text.length;
        const span = text.slice(start, nextStart);
        const close = span.indexOf(">");
        const tagRaw = close < 0 ? span : span.slice(0, close + 1);
        const body = close < 0 ? "" : span.slice(close + 1);
        tags.push({ tagRaw, body, ...classify(tagRaw) });
    }
    return { leading, tags };
};

const formatSeconds = (value: number): string =>
    Number.parseFloat(value.toFixed(3)).toString();

const authorClip = (index: number, clip: ClipTextInput | undefined): string => {
    if (!clip) {
        return "";
    }
    const pieces: string[] = [];
    const mainPrompt = (clip.prompt ?? "").trim();
    if (mainPrompt) {
        pieces.push(`<videoclip[${index}]>${mainPrompt}`);
    }
    const windows = [...(clip.windows ?? [])]
        .filter((w) => w.duration > 0)
        .sort((a, b) => a.start - b.start);
    for (const win of windows) {
        const start = Math.max(0, win.start);
        const startText = formatSeconds(start);
        const endText = formatSeconds(start + win.duration);
        const winPrompt = (win.prompt ?? "").trim();
        pieces.push(
            `<videoclip[${index}]:${startText}-${endText}>${winPrompt}`,
        );
    }
    return pieces.join("\n");
};

export const serializeClipPrompts = (
    prompt: string,
    clips: ClipTextInput[],
): string => {
    const { leading, tags } = tokenizePrompt(prompt);
    const blocks: string[] = [];
    const leadTrimmed = leading.trimEnd();
    if (leadTrimmed) {
        blocks.push(leadTrimmed);
    }
    const emitted = new Set<number>();
    const emitClip = (index: number): void => {
        if (emitted.has(index)) {
            return;
        }
        emitted.add(index);
        const block = authorClip(index, clips[index]);
        if (block) {
            blocks.push(block);
        }
    };
    for (const tag of tags) {
        if (tag.owned !== null) {
            const index = tag.clip ?? -1;
            if (index >= 0 && index < clips.length) {
                emitClip(index);
            }
            continue;
        }
        const raw = (tag.tagRaw + tag.body).trimEnd();
        if (raw) {
            blocks.push(raw);
        }
    }
    for (let index = 0; index < clips.length; index++) {
        emitClip(index);
    }
    return blocks.join("\n");
};

export const parseClipPrompts = (prompt: string): ParsedClipPrompts => {
    const { tags } = tokenizePrompt(prompt);
    const sections = new Map<number, string>();
    const windows = new Map<number, ParsedWindow[]>();
    for (const tag of tags) {
        if (tag.owned === "section" && tag.clip !== null) {
            if (!sections.has(tag.clip)) {
                sections.set(tag.clip, tag.body.trim());
            }
        } else if (tag.owned === "window" && tag.clip !== null && tag.window) {
            const list = windows.get(tag.clip) ?? [];
            list.push({
                start: Math.max(0, tag.window.start),
                duration: tag.window.end - tag.window.start,
                prompt: tag.body.trim(),
            });
            windows.set(tag.clip, list);
        }
    }
    for (const list of windows.values()) {
        list.sort((a, b) => a.start - b.start);
    }
    return { sections, windows };
};

export const extractGlobalPrompt = (prompt: string): string =>
    tokenizePrompt(prompt).leading.trim();
