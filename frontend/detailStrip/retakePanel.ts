import {
    clamp,
    RETAKE_DURATION_STEP,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_MAX,
    RETAKE_STRENGTH_MIN,
    RETAKE_STRENGTH_STEP,
} from "../constants";
import {
    appendHelp,
    buildAccordionSection,
    buildDetailActionButton,
    buildField,
    buildSlider,
} from "../detailWidgets";
import { clampStartLength } from "../trimGeometry";
import type { Clip } from "../types";
import type { DetailStripContext } from "./context";

export const buildRetakeSection = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    open = false,
): HTMLElement => {
    const retake = clip.retake;
    const decision = context
        .authoring()
        .capabilities.forClip(clip)
        .decision("retake");
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-retake-col";
    const buildSection = (): HTMLElement => {
        const built = buildAccordionSection({
            key: "retake",
            label: "Retake",
            className: "vst-detail-retake-section",
            open,
            content: col,
            flattenContent: true,
            headerAction: retake
                ? {
                      label: "×",
                      title: "Delete the retake window",
                      className: "vst-detail-delete vst-detail-delete-retake",
                      variant: "interrupt",
                      onClick: () => context.removeRetake(clipIdx),
                  }
                : undefined,
        });
        appendHelp(
            built.heading,
            built.section,
            "Retake",
            "Regenerate just a time window of a base video, leaving the rest " +
                "untouched — handy for fixing one bad stretch without redoing " +
                "the whole clip.",
        );
        return built.section;
    };
    if (!retake) {
        const hint = document.createElement("small");
        hint.className = "vst-detail-field-hint";
        hint.textContent =
            "Regenerates a sub-range when refining a base video.";
        col.appendChild(hint);
        const add = buildDetailActionButton({
            label: "+ Add Retake",
            title: decision.supported ? "Add a retake window" : decision.reason,
            className:
                "small-button vst-detail-repeating-add vst-detail-add-retake",
            disabled: !decision.supported,
            onClick: () => context.createRetake(clipIdx),
        });
        col.appendChild(add);
        return buildSection();
    }

    const clipDuration = Math.max(RETAKE_MIN_DURATION, clip.duration || 0);
    const clampRetake = (
        start: number,
        length: number,
    ): { start: number; length: number } =>
        clampStartLength(start, length, clipDuration, RETAKE_MIN_DURATION);
    const startInput = context.buildClampedNumber({
        key: "retake-start",
        value: retake.startSeconds,
        min: 0,
        max: Math.max(0, clipDuration - RETAKE_MIN_DURATION),
        step: RETAKE_DURATION_STEP,
        readBack: (clips) => clips[clipIdx]?.retake?.startSeconds ?? null,
        mutate: (clips, value) => {
            const target = clips[clipIdx]?.retake;
            if (target) {
                const next = clampRetake(value, target.lengthSeconds);
                target.startSeconds = next.start;
                target.lengthSeconds = next.length;
            }
        },
    });
    col.appendChild(
        buildField(
            "Start (s)",
            startInput,
            undefined,
            "Where the retake window begins inside the clip. Only this " +
                "sub-range is regenerated.",
        ),
    );

    const lengthInput = context.buildClampedNumber({
        key: "retake-length",
        value: retake.lengthSeconds,
        min: RETAKE_MIN_DURATION,
        max: clipDuration,
        step: RETAKE_DURATION_STEP,
        readBack: (clips) => clips[clipIdx]?.retake?.lengthSeconds ?? null,
        mutate: (clips, value) => {
            const target = clips[clipIdx]?.retake;
            if (target) {
                const next = clampRetake(target.startSeconds, value);
                target.startSeconds = next.start;
                target.lengthSeconds = next.length;
            }
        },
    });
    col.appendChild(
        buildField(
            "Length (s)",
            lengthInput,
            undefined,
            "How long the retake window is, starting at Start. Frames outside " +
                "the window are kept as-is.",
        ),
    );
    col.appendChild(
        buildSlider(
            "Strength",
            retake.strength,
            RETAKE_STRENGTH_MIN,
            RETAKE_STRENGTH_MAX,
            RETAKE_STRENGTH_STEP,
            (value) => {
                context.debouncedCommit("retake-strength", (clips) => {
                    const target = clips[clipIdx]?.retake;
                    if (target) {
                        target.strength = clamp(
                            value,
                            RETAKE_STRENGTH_MIN,
                            RETAKE_STRENGTH_MAX,
                        );
                    }
                });
            },
            {
                help:
                    "How much of the window is regenerated. Higher changes the " +
                    "footage more; lower keeps it closer to the original.",
            },
        ),
    );

    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent =
        "Applies when refining a base video; audio inside the window regenerates with the frames.";
    col.appendChild(note);

    return buildSection();
};
