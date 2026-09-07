import { PROMPT_WINDOW_MIN_DURATION } from "../constants";
import {
    buildField,
    buildTextarea,
    type TextareaLayout,
} from "../detailWidgets";
import {
    applyPromptWindowBegin,
    applyPromptWindowEnd,
    promptWindowNeighborBounds,
} from "../promptWindowEdits";
import { setSelection } from "../selection";
import { readGlobalPrompt } from "../swarmInputs";
import type { Clip } from "../types";
import { gridCeil, gridFloor, roundToTenth } from "../utils";
import type { DetailStripContext } from "./context";

export const buildMajorPromptFields = (
    ctx: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    layout: TextareaLayout = { rows: 8 },
): HTMLElement => {
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-prompt-body";
    const prompt = buildTextarea(
        clip.prompt ?? "",
        readGlobalPrompt().trim() ||
            "Clip prompt (blank inherits the global prompt)…",
        "prompt-major",
        (value) => {
            ctx.debouncedCommit("prompt-major", (clips) => {
                const target = clips[clipIdx];
                if (target) {
                    target.prompt = value.trim();
                }
            });
        },
        layout,
    );
    col.appendChild(buildField("Prompt", prompt));
    return col;
};

export const buildRelayPromptFields = (
    ctx: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    windowIdx: number,
): HTMLElement => {
    const window = clip.promptWindows[windowIdx];
    if (!window) {
        throw new Error(`Relay prompt ${windowIdx} does not exist`);
    }
    const clipDuration = Math.max(
        PROMPT_WINDOW_MIN_DURATION,
        clip.duration || 0,
    );
    const fields = document.createElement("div");
    fields.className =
        "vst-detail-col vst-detail-fields vst-detail-prompt-body vst-detail-minor-window";
    fields.setAttribute("data-vst-minor-window", `${windowIdx}`);
    const bounds = promptWindowNeighborBounds(clip, windowIdx);
    const beginInput = ctx.buildClampedNumber({
        key: `minor-${windowIdx}-begin`,
        value: roundToTenth(window.start),
        min: bounds?.beginMin ?? 0,
        max: gridFloor(Math.max(0, clipDuration - PROMPT_WINDOW_MIN_DURATION)),
        step: 0.1,
        readBack: (clips) => {
            const target = clips[clipIdx]?.promptWindows?.[windowIdx];
            return target ? roundToTenth(target.start) : null;
        },
        mutate: (clips, value) => {
            const target = clips[clipIdx];
            if (target) {
                applyPromptWindowBegin(target, windowIdx, value);
            }
        },
    });
    fields.appendChild(
        buildField(
            "Begin (s)",
            beginInput,
            undefined,
            "When this relay prompt starts within the clip.",
        ),
    );
    const endInput = ctx.buildClampedNumber({
        key: `minor-${windowIdx}-end`,
        value: roundToTenth(window.start + window.duration),
        min: gridCeil(PROMPT_WINDOW_MIN_DURATION),
        max: bounds?.endMax ?? clipDuration,
        step: 0.1,
        readBack: (clips) => {
            const target = clips[clipIdx]?.promptWindows?.[windowIdx];
            return target ? roundToTenth(target.start + target.duration) : null;
        },
        mutate: (clips, value) => {
            const target = clips[clipIdx];
            if (target) {
                applyPromptWindowEnd(target, windowIdx, value);
            }
        },
    });
    fields.appendChild(
        buildField(
            "End (s)",
            endInput,
            undefined,
            "When this relay prompt ends. It cannot cross a neighbouring relay.",
        ),
    );
    const editor = buildTextarea(
        window.prompt ?? "",
        "Relay prompt for this window…",
        `minor-${windowIdx}`,
        (value) => {
            ctx.debouncedCommit(`minor-${windowIdx}`, (clips) => {
                const target = clips[clipIdx]?.promptWindows?.[windowIdx];
                if (target) {
                    target.prompt = value.trim();
                }
            });
        },
        { rows: 4 },
    );
    editor.addEventListener("focus", () => {
        setSelection({ kind: "prompt-minor", clipIdx, windowIdx });
    });
    const prompt = buildField("Prompt", editor);
    prompt.classList.add("vst-detail-span-full");
    fields.appendChild(prompt);
    return fields;
};
