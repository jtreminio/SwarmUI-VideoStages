import { type BoundaryPlan, crossfadePlanForClips } from "../boundaryPlan";
import {
    buildField,
    buildOptionSelect,
    type OptionSpec,
    wrapForm,
} from "../detailWidgets";
import { getState } from "../persistence";
import { formatOverlapSeconds } from "../timelineDetail";
import { BOUNDARY_GLYPH, BOUNDARY_LABEL } from "../timelineView";
import type { BoundaryOut, Clip, TimelineSelection } from "../types";
import type { DetailStripContext } from "./context";

const GROUP_BOUNDARY = "vstdock_boundary";

export const buildBoundaryBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "boundary" }>,
    clips: Clip[],
): HTMLElement => {
    const { leftClipIdx } = sel;
    const body = document.createElement("div");
    body.className = "vst-detail-form-body vst-detail-boundary";
    const clip = clips[leftClipIdx];
    const value: BoundaryOut = clip?.boundaryOut ?? "cut";
    const state = getState();
    const fps = state.fps > 0 ? Math.round(state.fps) : 24;

    const joinSpecs: OptionSpec[] = (
        ["cut", "continue", "crossfade"] as BoundaryOut[]
    ).map((mode) => ({
        value: mode,
        label: `${BOUNDARY_LABEL[mode]} ${BOUNDARY_GLYPH[mode]}`,
    }));
    const select = buildOptionSelect(joinSpecs, value, (next) => {
        ctx.commit((cs) => {
            const c = cs[leftClipIdx];
            if (c) {
                c.boundaryOut = (next as BoundaryOut) ?? "cut";
            }
        });
        ctx.render();
    });
    body.appendChild(
        buildField(`Join · Clip ${leftClipIdx} → ${leftClipIdx + 1}`, select),
    );

    const info = document.createElement("div");
    info.className = "vst-boundary-info";
    if (value === "cut") {
        info.textContent = "Hard cut — clips are concatenated with no overlap.";
    } else if (value === "continue") {
        info.textContent =
            `Continue — 1 frame (~${formatOverlapSeconds(1, fps)}) overlap. ` +
            "The next clip generates from this clip's final frame and the merge collapses the duplicated seam frame.";
    } else {
        const plan: BoundaryPlan = crossfadePlanForClips(clips, fps);
        const overlapFrames = plan.overlaps[leftClipIdx] ?? 0;
        if (plan.fallback || overlapFrames <= 0) {
            info.classList.add("vst-boundary-warn");
            info.textContent =
                "This crossfade will fall back to a cut — a clip is too short for the overlap window.";
        } else {
            info.textContent =
                `Crossfade — ${overlapFrames} frame${overlapFrames === 1 ? "" : "s"} ` +
                `(~${formatOverlapSeconds(overlapFrames, fps)}) pixel dissolve.`;
        }
    }
    body.appendChild(info);

    if (value !== "cut") {
        const note = document.createElement("div");
        note.className = "vst-boundary-note";
        note.textContent =
            "Requires the LTX-2 model family — the backend degrades this boundary to a cut otherwise.";
        body.appendChild(note);
    }
    return wrapForm(GROUP_BOUNDARY, body);
};
