import type { AuthoringState } from "../architectures/policy";
import { clipLengthReferenceIndex } from "../clipReferenceAuthoring";
import { CLIP_DURATION_MAX, CLIP_DURATION_MIN } from "../constants";
import {
    buildField,
    buildNumber,
    buildOptionSelect,
    type SectionHeaderAction,
} from "../detailWidgets";
import { skipGlyph, skipTitle } from "../skipVocabulary";
import { applyClipDurationResize } from "../timelineEdit";
import type { Clip, ReferenceFraming } from "../types";
import { applyPersistedCapabilityRepair } from "./capabilityUi";
import type { DetailStripContext } from "./context";

const DURATION_STEP = 0.1;
const REFERENCE_LENGTH_HINT = "(derived from a reference's media length)";

export const buildClipColumn = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    referenceFramingState?: AuthoringState,
): HTMLElement => {
    const column = document.createElement("div");
    column.className =
        "input-group-content vst-detail-section-content vst-detail-col vst-detail-clip";

    const initVideoClip = !!clip.initVideo;
    const lengthReferenceIdx = clipLengthReferenceIndex(clip.references);
    const lengthDerived =
        clip.clipLengthFromAudio === true ||
        clip.clipLengthFromControlNet === true ||
        lengthReferenceIdx >= 0 ||
        initVideoClip;
    const durationInput = buildNumber(
        clip.duration,
        CLIP_DURATION_MIN,
        CLIP_DURATION_MAX,
        DURATION_STEP,
        (value) => {
            context.debouncedCommit("duration", (clips) => {
                const target = clips[clipIdx];
                if (target && !lengthDerived) {
                    const defaults = context.authoring().defaults;
                    applyClipDurationResize(target, value, defaults);
                }
            });
        },
    );
    durationInput.setAttribute("data-vst-focus-key", "duration");
    const durationHint = !lengthDerived
        ? null
        : initVideoClip
          ? "(derived from the source video range)"
          : lengthReferenceIdx >= 0
            ? REFERENCE_LENGTH_HINT
            : "(derived from audio/ControlNet source)";
    const durationField = buildField(
        "Duration (s)",
        durationInput,
        durationHint ?? REFERENCE_LENGTH_HINT,
    );
    durationField
        .querySelector(".vst-detail-field-hint")
        ?.classList.toggle("vst-detail-field-hint-hidden", !durationHint);
    if (lengthDerived) {
        durationInput.disabled = true;
        durationField.classList.add("vst-field-disabled");
    }
    column.appendChild(durationField);
    if (referenceFramingState?.visible) {
        const framing = buildOptionSelect(
            [
                { value: "crop", label: "Crop" },
                { value: "stretch", label: "Stretch" },
                { value: "fit", label: "Fit (black padding)" },
                { value: "fit-green", label: "Fit (green padding)" },
            ],
            clip.refFraming,
            (value) => {
                context.commit((clips) => {
                    const target = clips[clipIdx];
                    if (target) {
                        target.refFraming = value as ReferenceFraming;
                    }
                });
            },
        );
        framing.dataset.vstReferenceFraming = "true";
        const field = buildField(
            "Reference resize",
            framing,
            undefined,
            "Fit (green padding) preserves aspect ratio and pads with #66FF00 so outpainting IC-LoRAs treat the padded area as empty.",
        );
        if (!referenceFramingState.enabled) {
            applyPersistedCapabilityRepair(field, referenceFramingState, {
                repair: {
                    label: "Reset reference resize",
                    className: "vst-reset-unsupported-reference-framing",
                    onRepair: () => {
                        context.commit((clips) => {
                            const target = clips[clipIdx];
                            if (target) {
                                target.refFraming = "crop";
                            }
                        });
                    },
                },
            });
        }
        column.appendChild(field);
    }
    return column;
};

export const buildClipSkipAction = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
): SectionHeaderAction => ({
    label: skipGlyph(clip.skipped === true),
    title: skipTitle("clip", clip.skipped === true),
    className: "vst-detail-skip-clip",
    active: clip.skipped === true,
    onClick: () => context.toggleClipSkip(clipIdx),
});
