import {
    type BoundaryPlan,
    CONTINUE_OVERLAP_CHOICES,
    crossfadePlanForClips,
    DEFAULT_CONTINUE_OVERLAP_FRAMES,
} from "../boundaryPlan";
import {
    buildField,
    buildOptionSelect,
    type OptionSpec,
    wrapForm,
} from "../detailWidgets";
import { normalizeContinueOverlap } from "../normalization";
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
        buildField(
            `Join · Clip ${leftClipIdx} → ${leftClipIdx + 1}`,
            select,
            undefined,
            "How this clip joins the next one. Cut: hard concatenation. " +
                "Continue: the next clip is generated from this clip's last " +
                "frames so motion carries through. Crossfade: the overlap is " +
                "dissolved pixel-by-pixel.",
        ),
    );

    if (value !== "cut") {
        const overlapValue =
            clip?.boundaryOutOverlap ?? DEFAULT_CONTINUE_OVERLAP_FRAMES;
        const overlapSpecs: OptionSpec[] = CONTINUE_OVERLAP_CHOICES.map(
            (frames) => ({
                value: `${frames}`,
                label: `${frames} frames (~${formatOverlapSeconds(frames, fps)})`,
            }),
        );
        const overlapSelect = buildOptionSelect(
            overlapSpecs,
            `${overlapValue}`,
            (next) => {
                ctx.commit((cs) => {
                    const c = cs[leftClipIdx];
                    if (c) {
                        c.boundaryOutOverlap = normalizeContinueOverlap(next);
                    }
                });
                ctx.render();
            },
        );
        body.appendChild(
            buildField(
                "Overlap",
                overlapSelect,
                undefined,
                "How many frames the two clips share at the join. For continue " +
                    "this is the frozen context handed to the next clip; for " +
                    "crossfade it is the length of the dissolve. A clip too " +
                    "short for the overlap falls back to a cut.",
            ),
        );
    }

    const info = document.createElement("div");
    info.className = "vst-boundary-info";
    if (value === "cut") {
        info.textContent = "Hard cut — clips are concatenated with no overlap.";
    } else if (value === "continue") {
        const plan: BoundaryPlan = crossfadePlanForClips(clips, fps);
        const window = plan.overlaps[leftClipIdx] ?? 0;
        if (plan.fallback || window <= 0) {
            info.classList.add("vst-boundary-warn");
            info.textContent =
                "This continue will fall back to a cut — a clip is too short for the overlap.";
        } else {
            const requested =
                (clip?.boundaryOutOverlap ?? DEFAULT_CONTINUE_OVERLAP_FRAMES) +
                1;
            let text =
                `Continue — the next clip is generated with this clip's last ${window} ` +
                `frame${window === 1 ? "" : "s"} (~${formatOverlapSeconds(window, fps)}) as frozen ` +
                "context, and the merge collapses the duplicated frames.";
            if (window < requested) {
                text += " The window was reduced because a clip is too short.";
            }
            info.textContent = text;
        }
    } else {
        const plan: BoundaryPlan = crossfadePlanForClips(clips, fps);
        const overlapFrames = plan.overlaps[leftClipIdx] ?? 0;
        if (plan.fallback || overlapFrames <= 0) {
            info.classList.add("vst-boundary-warn");
            info.textContent =
                "This crossfade will fall back to a cut — a clip is too short for the overlap window.";
        } else {
            const requested =
                clip?.boundaryOutOverlap ?? DEFAULT_CONTINUE_OVERLAP_FRAMES;
            let text =
                `Crossfade — ${overlapFrames} frame${overlapFrames === 1 ? "" : "s"} ` +
                `(~${formatOverlapSeconds(overlapFrames, fps)}) pixel dissolve.`;
            if (overlapFrames < requested) {
                text += " The window was reduced because a clip is too short.";
            }
            info.textContent = text;
        }
    }
    body.appendChild(info);

    return wrapForm(GROUP_BOUNDARY, body);
};
