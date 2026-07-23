import { CLIP_DURATION_MAX, CLIP_DURATION_MIN } from "../constants";
import { buildCheckbox, buildField, buildNumber } from "../detailWidgets";
import { getRootDefaults } from "../rootDefaults";
import { applyClipDurationResize } from "../timelineEdit";
import type { Clip } from "../types";
import type { DetailStripContext } from "./context";

const DURATION_STEP = 0.1;

export const buildClipColumn = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
): HTMLElement => {
    const column = document.createElement("div");
    column.className = "vst-detail-col vst-detail-clip";

    const sourced = !!clip.sourceVideo;
    const lengthDerived =
        clip.clipLengthFromAudio === true ||
        clip.clipLengthFromControlNet === true ||
        sourced;
    const durationInput = buildNumber(
        clip.duration,
        CLIP_DURATION_MIN,
        CLIP_DURATION_MAX,
        DURATION_STEP,
        (value) => {
            context.debouncedCommit("duration", (clips) => {
                const target = clips[clipIdx];
                if (target && !lengthDerived) {
                    applyClipDurationResize(target, value, getRootDefaults);
                }
            });
        },
    );
    durationInput.setAttribute("data-vst-focus-key", "duration");
    const durationField = buildField(
        "Duration (s)",
        durationInput,
        lengthDerived
            ? sourced
                ? "(derived from the source video range)"
                : "(derived from audio/ControlNet source)"
            : undefined,
    );
    if (lengthDerived) {
        durationInput.disabled = true;
        durationField.classList.add("vst-field-disabled");
    }
    column.appendChild(durationField);
    column.appendChild(
        buildCheckbox("Skip this clip", clip.skipped === true, (value) => {
            context.commit((clips) => {
                const target = clips[clipIdx];
                if (target) {
                    target.skipped = value;
                }
            });
        }),
    );
    return column;
};
