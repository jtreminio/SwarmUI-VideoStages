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
    buildField,
    buildRepeatingEditor,
    buildSlider,
    clampStartLength,
} from "../detailWidgets";
import { setSelection } from "../selection";
import type { Clip } from "../types";
import type { DetailStripContext } from "./context";

export const buildRetakeSection = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
): HTMLElement => {
    const retake = clip.retake;
    const decision = context.capabilities().forClip(clip).decision("retake");
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-retake-col";
    const buildSection = (): HTMLElement => {
        const built = buildRepeatingEditor({
            key: "retakes",
            label: "Retake",
            sectionClass: "vst-detail-retake-section",
            listClass: "vst-detail-retake-rail",
            items: retake
                ? [
                      {
                          label: "RT",
                          title: "Edit retake window",
                          active: true,
                          className: "vst-retake-tab",
                          onSelect: () =>
                              setSelection({ kind: "retake", clipIdx }),
                          onDelete: () => context.removeRetake(clipIdx),
                      },
                  ]
                : [],
            add: {
                title: retake
                    ? "This clip already has a retake window"
                    : decision.supported
                      ? "Add a retake window"
                      : decision.reason,
                className: "vst-detail-add-retake",
                disabled: !!retake || !decision.supported,
                onClick: () => context.createRetake(clipIdx),
            },
            remove: {
                title: retake
                    ? "Delete the retake window"
                    : "No retake window to delete",
                className: "vst-detail-delete-retake",
            },
            editor: col,
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
