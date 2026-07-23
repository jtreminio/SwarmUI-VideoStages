import {
    AUDIO_SOURCE_UPLOAD,
    buildSegmentAudioSourceOptions,
    isAceStepFunAudioSource,
} from "../audioSource";
import {
    AUDIO_SEGMENT_MIN_LENGTH,
    AUDIO_SEGMENT_STEP,
    AUDIO_SEGMENT_VOLUME_MAX,
    AUDIO_SEGMENT_VOLUME_MIN,
    AUDIO_SEGMENT_VOLUME_SLIDER_MAX,
    AUDIO_SEGMENT_VOLUME_SLIDER_MIN,
    AUDIO_SEGMENT_VOLUME_SLIDER_STEP,
    CLIP_DURATION_MAX,
} from "../constants";
import {
    buildField,
    buildInstanceRow,
    buildMediaPickRow,
    buildNumber,
    buildOptionSelect,
    buildSlider,
    clampStartLength,
    wrapForm,
} from "../detailWidgets";
import { setSelection } from "../selection";
import type { Clip, TimelineSelection } from "../types";
import { disableCapabilityControls } from "./capabilityUi";
import type { DetailStripContext } from "./context";

const GROUP_AUDIOSEG = "vstdock_audioseg";

/**
 * The audio-segment panel lists EVERY overlay segment of the clip, stacked.
 * The selected segment is highlighted; touching any segment's control
 * re-points the selection to it (targeted swap, no rebuild) and per-segment
 * keys keep edits distinct.
 */
export const buildAudioSegmentBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "audio-segment" }>,
    clips: Clip[],
): HTMLElement => {
    const { clipIdx } = sel;
    const clip = clips[clipIdx];
    const segments = clip?.audioSegments ?? [];
    const body = document.createElement("div");
    body.className =
        "vst-detail-form-body vst-detail-instance-body vst-detail-seg-body";
    const clipDur = Math.max(AUDIO_SEGMENT_MIN_LENGTH, clip?.duration || 0);

    /**
     * Segments live on per-segment lanes and may overlap in time (the
     * backend mixes them additively) — start/length clamp only to the
     * clip's own bounds.
     */
    const clampSegment = (
        start: number,
        length: number,
    ): { start: number; length: number } =>
        clampStartLength(start, length, clipDur, AUDIO_SEGMENT_MIN_LENGTH);

    segments.forEach((segment, segIdx) => {
        const { row, fields } = buildInstanceRow({
            rowClass: "vst-detail-seg-row",
            indexAttr: "data-vst-seg-index",
            index: segIdx,
            active: segIdx === sel.segIdx,
            title: `S${segIdx + 1}`,
            deleteLabel: "Remove segment",
            onDelete: () => ctx.removeAudioSegment(clipIdx, segIdx),
            repoint: () =>
                setSelection({ kind: "audio-segment", clipIdx, segIdx }),
        });

        // Source select: an upload, or an AceStepFun generated track ref.
        const segSourceRef =
            typeof segment.source === "string" ? segment.source : "";
        const segSourceValue = segSourceRef || AUDIO_SOURCE_UPLOAD;
        const segSourceSelect = buildOptionSelect(
            buildSegmentAudioSourceOptions(segSourceRef),
            segSourceValue,
            (value) => {
                ctx.commit((cs) => {
                    const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                    if (!seg) {
                        return;
                    }
                    if (isAceStepFunAudioSource(value)) {
                        seg.source = value;
                    } else if (typeof seg.source === "string") {
                        seg.source = null;
                    }
                });
                ctx.render();
            },
        );
        fields.appendChild(
            buildField(
                "Source",
                segSourceSelect,
                undefined,
                "Where this overlay segment's audio comes from — an uploaded " +
                    "file or a generated track. It is mixed on top of the " +
                    "clip's base audio.",
            ),
        );

        if (!segSourceRef) {
            fields.appendChild(
                buildMediaPickRow(
                    "Audio Upload",
                    "audio/*",
                    ["audio"],
                    typeof segment.source === "string"
                        ? undefined
                        : segment.source?.fileName,
                    (data, fileName) => {
                        ctx.commit((cs) => {
                            const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                            if (seg) {
                                seg.source = { data, fileName };
                            }
                        });
                        ctx.render();
                    },
                    () => {
                        ctx.commit((cs) => {
                            const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                            if (seg) {
                                seg.source = null;
                            }
                        });
                        ctx.render();
                    },
                ),
            );
        }

        const volumeSlider = buildSlider(
            "Volume",
            segment.volume,
            AUDIO_SEGMENT_VOLUME_MIN,
            AUDIO_SEGMENT_VOLUME_MAX,
            AUDIO_SEGMENT_VOLUME_SLIDER_STEP,
            (value) => {
                ctx.debouncedCommit(`seg-${segIdx}-volume`, (cs) => {
                    const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                    if (seg) {
                        seg.volume = Math.min(
                            AUDIO_SEGMENT_VOLUME_MAX,
                            Math.max(AUDIO_SEGMENT_VOLUME_MIN, value),
                        );
                    }
                });
            },
            {
                sliderMin: AUDIO_SEGMENT_VOLUME_SLIDER_MIN,
                sliderMax: AUDIO_SEGMENT_VOLUME_SLIDER_MAX,
                numberStep: "any",
                help:
                    "Relative loudness before this segment is mixed over the clip. " +
                    "1 keeps its original level, values below 1 make it quieter, " +
                    "and values above 1 make it louder. The slider covers 0.1–4; " +
                    "the number input accepts 0.00001–100000 (-100 dB to +100 dB).",
            },
        );
        volumeSlider
            .querySelector<HTMLInputElement>("input.auto-slider-number")
            ?.setAttribute("data-vst-focus-key", `seg-${segIdx}-volume`);
        fields.appendChild(volumeSlider);

        const startInput = ctx.buildClampedNumber({
            key: `seg-${segIdx}-start`,
            value: segment.startSeconds,
            min: 0,
            max: Math.max(0, clipDur - AUDIO_SEGMENT_MIN_LENGTH),
            step: AUDIO_SEGMENT_STEP,
            readBack: (cs) =>
                cs[clipIdx]?.audioSegments?.[segIdx]?.startSeconds ?? null,
            mutate: (cs, value) => {
                const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                if (seg) {
                    const next = clampSegment(value, seg.lengthSeconds);
                    seg.startSeconds = next.start;
                    seg.lengthSeconds = next.length;
                }
            },
        });
        fields.appendChild(
            buildField(
                "Start (s)",
                startInput,
                undefined,
                "When this segment begins on the clip's timeline, in seconds " +
                    "from the clip start.",
            ),
        );

        const trimInput = buildNumber(
            segment.trimStartSeconds,
            0,
            CLIP_DURATION_MAX,
            AUDIO_SEGMENT_STEP,
            (value) => {
                ctx.debouncedCommit(`seg-${segIdx}-trim`, (cs) => {
                    const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                    if (seg) {
                        seg.trimStartSeconds = Math.max(
                            0,
                            Math.round(value * 10) / 10,
                        );
                    }
                });
            },
        );
        trimInput.setAttribute("data-vst-focus-key", `seg-${segIdx}-trim`);
        fields.appendChild(
            buildField(
                "Trim start (s)",
                trimInput,
                undefined,
                "Skip this many seconds from the beginning of the source audio " +
                    "before it starts playing — lets you use a later portion of " +
                    "the file.",
            ),
        );

        const lengthInput = ctx.buildClampedNumber({
            key: `seg-${segIdx}-length`,
            value: segment.lengthSeconds,
            min: AUDIO_SEGMENT_MIN_LENGTH,
            max: clipDur,
            step: AUDIO_SEGMENT_STEP,
            readBack: (cs) =>
                cs[clipIdx]?.audioSegments?.[segIdx]?.lengthSeconds ?? null,
            mutate: (cs, value) => {
                const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                if (seg) {
                    const next = clampSegment(seg.startSeconds, value);
                    seg.startSeconds = next.start;
                    seg.lengthSeconds = next.length;
                }
            },
        });
        fields.appendChild(
            buildField(
                "Length (s)",
                lengthInput,
                undefined,
                "How long this segment plays on the clip, in seconds.",
            ),
        );
        const decision = ctx
            .capabilities()
            .forClip(clip)
            .decision("audioSegments");
        if (!decision.supported) {
            disableCapabilityControls(row, decision, [".vst-detail-delete"]);
        }
        body.appendChild(row);
    });

    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent =
        "Overlaid additively over the base audio; overlapping segments mix together.";
    body.appendChild(note);

    return wrapForm(GROUP_AUDIOSEG, body);
};
