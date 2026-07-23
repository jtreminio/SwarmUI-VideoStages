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
    buildAddButton,
    buildField,
    buildSlider,
    buildStackSection,
    clampStartLength,
} from "../detailWidgets";
import type { Clip } from "../types";
import type { DetailStripContext } from "./context";

export const buildRetakeSection = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
): HTMLElement => {
    const { wrap, col } = buildStackSection("Retake", "vst-detail-retake-col");
    const sectionLabel = wrap.querySelector<HTMLElement>(".vst-detail-sec");
    if (sectionLabel) {
        appendHelp(
            sectionLabel,
            wrap,
            "Retake",
            "Regenerate just a time window of a base video, leaving the rest " +
                "untouched — handy for fixing one bad stretch without redoing " +
                "the whole clip.",
        );
    }
    const retake = clip.retake;
    if (!retake) {
        const hint = document.createElement("small");
        hint.className = "vst-audio-field-hint";
        hint.textContent =
            "Regenerates a sub-range when refining a base video.";
        col.append(
            hint,
            buildAddButton("Add retake", "vst-detail-add-retake", () =>
                context.createRetake(clipIdx),
            ),
        );
        return wrap;
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

    const removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.className =
        "basic-button small-button vst-refs-delete vst-detail-delete vst-detail-rail-btn";
    removeButton.textContent = "Remove retake";
    removeButton.addEventListener("click", (event) => {
        event.preventDefault();
        context.removeRetake(clipIdx);
    });
    col.appendChild(removeButton);
    return wrap;
};
