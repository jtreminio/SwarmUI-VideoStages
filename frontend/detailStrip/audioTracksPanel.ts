import {
    buildAddButton,
    buildField,
    buildInstanceRow,
    buildOptionSelect,
    buildStackSection,
    type OptionSpec,
} from "../detailWidgets";
import { createEntityId } from "../identity";
import type {
    AudioTrack,
    AudioTrackSourceKind,
    AudioTrackSpan,
    VideoStagesConfig,
} from "../types";
import type { DetailStripContext } from "./context";

const SOURCE_KINDS: AudioTrackSourceKind[] = [
    "Upload",
    "AceStepFun",
    "Native",
    "ControlNet",
    "External",
];

const commitTrack = (
    ctx: DetailStripContext,
    trackId: string,
    mutate: (track: AudioTrack) => void,
): void => {
    ctx.commitState((state) => {
        const track = state.audioTracks?.find((entry) => entry.id === trackId);
        if (track) {
            mutate(track);
        }
    });
};

const commitSpan = (
    ctx: DetailStripContext,
    trackId: string,
    spanId: string,
    mutate: (span: AudioTrackSpan) => void,
): void =>
    commitTrack(ctx, trackId, (track) => {
        const span = track.spans.find((entry) => entry.id === spanId);
        if (span) {
            mutate(span);
        }
    });

const buildTextInput = (
    value: string,
    focusKey: string,
    onInput: (value: string) => void,
): HTMLInputElement => {
    const input = document.createElement("input");
    input.type = "text";
    input.className = "auto-text vst-audio-track-reference";
    input.value = value;
    input.setAttribute("data-vst-focus-key", focusKey);
    input.addEventListener("input", () => onInput(input.value));
    return input;
};

const buildOptionalNumber = (
    value: number | null,
    focusKey: string,
    onChange: (value: number | null) => void,
): HTMLInputElement => {
    const input = document.createElement("input");
    input.type = "number";
    input.className = "auto-number vst-audio-track-number";
    input.min = "0";
    input.step = "0.1";
    input.value = value === null ? "" : `${value}`;
    input.placeholder = "Optional";
    input.setAttribute("data-vst-focus-key", focusKey);
    input.addEventListener("change", () => {
        const raw = input.value.trim();
        if (!raw) {
            onChange(null);
            return;
        }
        const parsed = Number.parseFloat(raw);
        if (Number.isFinite(parsed) && parsed >= 0) {
            onChange(parsed);
        }
    });
    return input;
};

const buildRequiredNumber = (
    value: number,
    focusKey: string,
    onChange: (value: number) => void,
): HTMLInputElement => {
    const input = buildOptionalNumber(value, focusKey, (next) => {
        if (next !== null) {
            onChange(next);
        }
    });
    input.placeholder = "";
    return input;
};

const clipOptions = (
    state: VideoStagesConfig,
    selectedId: string | null,
    openLabel: string,
): OptionSpec[] => {
    const options: OptionSpec[] = [
        { value: "", label: openLabel },
        ...state.clips.map((clip, index) => ({
            value: clip.id as string,
            label: `Clip ${index + 1}`,
        })),
    ];
    if (selectedId && !state.clips.some((clip) => clip.id === selectedId)) {
        options.push({
            value: selectedId,
            label: `Missing clip (${selectedId})`,
        });
    }
    return options;
};

const buildSpanRow = (
    ctx: DetailStripContext,
    state: VideoStagesConfig,
    track: AudioTrack,
    span: AudioTrackSpan,
    spanIndex: number,
): HTMLElement => {
    const trackId = track.id as string;
    const spanId = span.id as string;
    const { row, fields } = buildInstanceRow({
        rowClass: "vst-audio-track-span",
        indexAttr: "data-vst-audio-span-index",
        index: spanIndex,
        active: false,
        title: `Span ${spanIndex + 1}`,
        deleteLabel: "Delete span",
        onDelete: () => {
            commitTrack(ctx, trackId, (nextTrack) => {
                nextTrack.spans = nextTrack.spans.filter(
                    (entry) => entry.id !== spanId,
                );
            });
            ctx.render();
        },
        repoint: () => {},
    });
    row.dataset.vstAudioSpanId = spanId;

    fields.append(
        buildField(
            "First clip (inclusive)",
            buildOptionSelect(
                clipOptions(state, span.firstClipId, "Timeline start (open)"),
                span.firstClipId ?? "",
                (value) =>
                    commitSpan(ctx, trackId, spanId, (next) => {
                        next.firstClipId = value || null;
                    }),
            ),
            "Leave open to begin at the timeline start. If both clip endpoints " +
                "are open, provide a complete timeline start and length.",
        ),
        buildField(
            "Last clip (inclusive)",
            buildOptionSelect(
                clipOptions(state, span.lastClipId, "Timeline end (open)"),
                span.lastClipId ?? "",
                (value) =>
                    commitSpan(ctx, trackId, spanId, (next) => {
                        next.lastClipId = value || null;
                    }),
            ),
            "Leave open to continue through the timeline end. The span may " +
                "cover one clip or a multi-clip range.",
        ),
        buildField(
            "Timeline start (s)",
            buildOptionalNumber(
                span.timelineStartSeconds,
                `audio-span-${spanId}-timeline-start`,
                (value) =>
                    commitSpan(ctx, trackId, spanId, (next) => {
                        next.timelineStartSeconds = value;
                    }),
            ),
        ),
        buildField(
            "Timeline length (s)",
            buildOptionalNumber(
                span.timelineLengthSeconds,
                `audio-span-${spanId}-timeline-length`,
                (value) =>
                    commitSpan(ctx, trackId, spanId, (next) => {
                        next.timelineLengthSeconds = value;
                    }),
            ),
            "Start and length are optional but validated together.",
        ),
        buildField(
            "Source start (s)",
            buildRequiredNumber(
                span.sourceStartSeconds,
                `audio-span-${spanId}-source-start`,
                (value) =>
                    commitSpan(ctx, trackId, spanId, (next) => {
                        next.sourceStartSeconds = value;
                    }),
            ),
        ),
        buildField(
            "Clip start offset (s)",
            buildOptionalNumber(
                span.clipStartOffsetSeconds,
                `audio-span-${spanId}-clip-start`,
                (value) =>
                    commitSpan(ctx, trackId, spanId, (next) => {
                        next.clipStartOffsetSeconds = value;
                    }),
            ),
        ),
        buildField(
            "Clip length (s)",
            buildOptionalNumber(
                span.clipLengthSeconds,
                `audio-span-${spanId}-clip-length`,
                (value) =>
                    commitSpan(ctx, trackId, spanId, (next) => {
                        next.clipLengthSeconds = value;
                    }),
            ),
            "Clip offset and length are optional but must be set together, " +
                "with the same first and last clip.",
        ),
    );
    return row;
};

const buildTrackRow = (
    ctx: DetailStripContext,
    state: VideoStagesConfig,
    track: AudioTrack,
    trackIndex: number,
): HTMLElement => {
    const trackId = track.id as string;
    const { row, fields } = buildInstanceRow({
        rowClass: "vst-audio-track",
        indexAttr: "data-vst-audio-track-index",
        index: trackIndex,
        active: false,
        title: `Track ${trackIndex + 1}`,
        deleteLabel: "Delete track",
        onDelete: () => {
            ctx.commitState((next) => {
                next.audioTracks = (next.audioTracks ?? []).filter(
                    (entry) => entry.id !== trackId,
                );
            });
            ctx.render();
        },
        repoint: () => {},
    });
    row.dataset.vstAudioTrackId = trackId;
    fields.append(
        buildField(
            "Source kind",
            buildOptionSelect(
                SOURCE_KINDS.map((kind) => ({
                    value: kind,
                    label: kind,
                })),
                track.source.kind,
                (value) =>
                    commitTrack(ctx, trackId, (next) => {
                        next.source.kind = value as AudioTrackSourceKind;
                    }),
            ),
        ),
        buildField(
            "Source reference",
            buildTextInput(
                track.source.reference,
                `audio-track-${trackId}-reference`,
                (value) =>
                    commitTrack(ctx, trackId, (next) => {
                        next.source.reference = value;
                    }),
            ),
            "Metadata only until the runtime mixer is connected. For Upload, " +
                "enter a file name or other durable reference; this editor " +
                "does not upload or mix the file yet.",
        ),
    );
    if (track.source.uploadedAudio) {
        const metadata = document.createElement("span");
        metadata.className = "vst-audio-track-upload-metadata";
        metadata.textContent =
            track.source.uploadedAudio.fileName?.trim() || "Embedded audio";
        fields.append(
            buildField(
                "Stored upload metadata",
                metadata,
                "Preserved for compatibility; planned tracks do not execute it yet.",
            ),
        );
    }

    const spans = document.createElement("div");
    spans.className = "vst-audio-track-spans";
    for (let index = 0; index < track.spans.length; index++) {
        spans.appendChild(
            buildSpanRow(ctx, state, track, track.spans[index], index),
        );
    }
    spans.appendChild(
        buildAddButton("Add span", "vst-audio-track-add-span", () => {
            const ownerId = state.clips[0]?.id ?? null;
            commitTrack(ctx, trackId, (next) => {
                next.spans.push({
                    id: createEntityId("audio_span"),
                    firstClipId: ownerId,
                    lastClipId: ownerId,
                    timelineStartSeconds: null,
                    timelineLengthSeconds: null,
                    sourceStartSeconds: 0,
                    clipStartOffsetSeconds: null,
                    clipLengthSeconds: null,
                });
            });
            ctx.render();
        }),
    );
    fields.appendChild(spans);
    return row;
};

export const buildAudioTracksPanel = (
    ctx: DetailStripContext,
    state: VideoStagesConfig,
): HTMLElement => {
    const { wrap, col } = buildStackSection(
        "Planned multi-clip audio",
        "vst-audio-tracks-col",
    );
    wrap.classList.add("vst-audio-tracks-panel");

    const warning = document.createElement("div");
    warning.className = "vst-audio-tracks-planned-warning";
    warning.textContent = "Planned — runtime mixer not yet connected";
    col.appendChild(warning);

    for (let index = 0; index < (state.audioTracks ?? []).length; index++) {
        col.appendChild(
            buildTrackRow(ctx, state, (state.audioTracks ?? [])[index], index),
        );
    }
    col.appendChild(
        buildAddButton("Add track", "vst-audio-track-add", () => {
            ctx.commitState((next) => {
                next.audioTracks ??= [];
                next.audioTracks.push({
                    id: createEntityId("audio_track"),
                    source: {
                        kind: "External",
                        reference: "",
                        uploadedAudio: null,
                    },
                    spans: [],
                });
            });
            ctx.render();
        }),
    );
    return wrap;
};
