import { PROMPT_WINDOW_MIN_DURATION } from "../constants";
import { buildField, buildTextarea, wrapForm } from "../detailWidgets";
import {
    applyPromptWindowBegin,
    applyPromptWindowEnd,
    promptWindowNeighborBounds,
} from "../promptWindowEdits";
import { setSelection } from "../selection";
import type { Clip, TimelineSelection } from "../types";
import { gridCeil, gridFloor, roundToTenth } from "../utils";
import { disableCapabilityControls } from "./capabilityUi";
import type { DetailStripContext } from "./context";

const GROUP_PROMPTMAJOR = "vstdock_promptmajor";
const GROUP_PROMPTMINOR = "vstdock_promptminor";

export const buildPromptMajorBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "prompt-major" }>,
    clips: Clip[],
): HTMLElement => {
    const { clipIdx } = sel;
    const body = document.createElement("div");
    body.className = "vst-detail-form-body vst-detail-prompt-body";
    body.appendChild(
        buildTextarea(
            clips[clipIdx].prompt ?? "",
            "Clip prompt (blank inherits the global prompt)…",
            "prompt-major",
            (value) => {
                ctx.debouncedCommit("prompt-major", (cs) => {
                    const c = cs[clipIdx];
                    if (c) {
                        c.prompt = value.trim();
                    }
                });
            },
        ),
    );
    const decision = ctx
        .capabilities()
        .forClip(clips[clipIdx])
        .decision("majorPrompt");
    if (!decision.supported) {
        disableCapabilityControls(body, decision);
        if (clips[clipIdx].prompt.trim()) {
            const clear = document.createElement("button");
            clear.type = "button";
            clear.className =
                "basic-button small-button vst-remove-unsupported-prompt";
            clear.textContent = "Remove unsupported clip prompt";
            clear.addEventListener("click", () => {
                ctx.commit((items) => {
                    const clip = items[clipIdx];
                    if (clip) {
                        clip.prompt = "";
                    }
                });
                ctx.render();
            });
            body.appendChild(clear);
        }
    }
    return wrapForm(GROUP_PROMPTMAJOR, body);
};

/**
 * Focusing another window's textarea re-points the selection so the timeline
 * highlight follows, without disrupting typing.
 */
export const buildPromptMinorBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "prompt-minor" }>,
    clips: Clip[],
): HTMLElement => {
    const { clipIdx, windowIdx } = sel;
    const clip = clips[clipIdx];
    const windows = clip?.promptWindows ?? [];
    const clipDur = Math.max(PROMPT_WINDOW_MIN_DURATION, clip?.duration || 0);
    const body = document.createElement("div");
    body.className =
        "vst-detail-form-body vst-detail-prompt-body vst-detail-minor-body";

    windows.forEach((w, idx) => {
        const row = document.createElement("div");
        row.className = "vst-detail-minor-window";
        row.setAttribute("data-vst-minor-window", `${idx}`);
        if (idx === windowIdx) {
            row.classList.add("vst-detail-minor-active");
        }

        const head = document.createElement("div");
        head.className = "vst-detail-minor-head";
        const title = document.createElement("span");
        title.className = "vst-detail-minor-title";
        title.textContent = `W${idx + 1}`;
        const del = document.createElement("button");
        del.type = "button";
        del.className =
            "basic-button small-button vst-refs-delete vst-detail-delete vst-detail-minor-delete";
        del.textContent = "Delete";
        del.title = "Delete this prompt window";
        del.addEventListener("click", (event) => {
            event.preventDefault();
            ctx.deleteWindowEntry(clipIdx, idx);
        });
        head.append(title, del);
        row.appendChild(head);

        /**
         * Begin/end are editable, reusing the timeline's edge-resize clamp:
         * begin holds the end fixed, end holds the begin fixed, both stay in
         * the clip, keep a minimum duration, and can't cross a neighbouring
         * window (applyPromptWindowBegin/End). The inputs' min/max are the
         * SAME neighbour-aware intervals, so spinner arrows stop AT the
         * neighbour instead of running past and snapping back on commit.
         * Distinct pending keys per edge per window; the commit repaints the
         * on-track segment.
         */
        const range = document.createElement("div");
        range.className = "vst-detail-minor-range";
        const bounds = clip ? promptWindowNeighborBounds(clip, idx) : null;

        /**
         * The browser's number spinner steps on a grid anchored at the
         * `min` attribute. A min of 0.25 puts whole-tenth values OFF the
         * 0.1 grid, so a down-spin snaps to x.95 — which roundSeconds
         * (half-up) rounds straight back to the original value: END could
         * never decrease. Keep the attrs ON the 0.1 grid (min rounded up,
         * max rounded down); the true 0.25-duration floor stays enforced
         * by the commit clamp (applyPromptWindowBegin/End).
         */
        const beginInput = ctx.buildClampedNumber({
            key: `minor-${idx}-begin`,
            value: roundToTenth(w.start),
            min: bounds?.beginMin ?? 0,
            max: gridFloor(Math.max(0, clipDur - PROMPT_WINDOW_MIN_DURATION)),
            step: 0.1,
            readBack: (cs) => {
                const win = cs[clipIdx]?.promptWindows?.[idx];
                return win ? roundToTenth(win.start) : null;
            },
            mutate: (cs, value) => {
                const c = cs[clipIdx];
                if (c) {
                    applyPromptWindowBegin(c, idx, value);
                }
            },
        });
        range.appendChild(
            buildField(
                "Begin (s)",
                beginInput,
                undefined,
                "When this prompt window starts within the clip, in seconds. " +
                    "Its prompt applies from here until End.",
            ),
        );

        const endInput = ctx.buildClampedNumber({
            key: `minor-${idx}-end`,
            value: roundToTenth(w.start + w.duration),
            min: gridCeil(PROMPT_WINDOW_MIN_DURATION),
            max: bounds?.endMax ?? clipDur,
            step: 0.1,
            readBack: (cs) => {
                const win = cs[clipIdx]?.promptWindows?.[idx];
                return win ? roundToTenth(win.start + win.duration) : null;
            },
            mutate: (cs, value) => {
                const c = cs[clipIdx];
                if (c) {
                    applyPromptWindowEnd(c, idx, value);
                }
            },
        });
        range.appendChild(
            buildField(
                "End (s)",
                endInput,
                undefined,
                "When this prompt window ends within the clip, in seconds. The " +
                    "window can't cross into a neighbouring window.",
            ),
        );
        row.appendChild(range);

        const editor = buildTextarea(
            w.prompt ?? "",
            "Minor prompt for this window…",
            `minor-${idx}`,
            (value) => {
                ctx.debouncedCommit(`minor-${idx}`, (cs) => {
                    const win = cs[clipIdx]?.promptWindows?.[idx];
                    if (win) {
                        win.prompt = value.trim();
                    }
                });
            },
        );
        /**
         * Focusing this window's editor makes it the active selection so the
         * timeline highlight follows. setSelection no-ops on an identical
         * selection, so this is a no-op while typing in the already-selected
         * window and never interrupts the caret.
         */
        editor.addEventListener("focus", () => {
            setSelection({ kind: "prompt-minor", clipIdx, windowIdx: idx });
        });
        row.appendChild(editor);
        const decision = ctx
            .capabilities()
            .forClip(clip)
            .decision("promptRelay");
        if (!decision.supported) {
            disableCapabilityControls(row, decision, [
                ".vst-detail-minor-delete",
            ]);
        }
        body.appendChild(row);
    });

    return wrapForm(GROUP_PROMPTMINOR, body);
};
