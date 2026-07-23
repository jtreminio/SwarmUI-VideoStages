import {
    buildField,
    buildOptionSelect,
    buildRepeatingEditor,
    type OptionSpec,
} from "../detailWidgets";
import { createEntityId } from "../identity";
import { setSelection } from "../selection";
import type {
    AudioTrack,
    AudioTrackSourceKind,
    AudioTrackSpan,
    TimelineSelection,
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

const buildSpanEditor = (
    ctx: DetailStripContext,
    state: VideoStagesConfig,
    track: AudioTrack,
    span: AudioTrackSpan,
): HTMLElement => {
    const trackId = track.id as string;
    const spanId = span.id as string;
    const fields = document.createElement("div");
    fields.className =
        "vst-detail-col vst-detail-instance-fields vst-audio-track-span";
    fields.dataset.vstAudioSpanId = spanId;

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
    return fields;
};

const buildTrackEditor = (
    ctx: DetailStripContext,
    state: VideoStagesConfig,
    track: AudioTrack,
    trackIndex: number,
    selectedSpanIndex: number | null,
): HTMLElement => {
    const trackId = track.id as string;
    const fields = document.createElement("div");
    fields.className =
        "vst-detail-col vst-detail-instance-fields vst-audio-track";
    fields.dataset.vstAudioTrackId = trackId;
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

    const activeSpanIndex =
        track.spans.length === 0
            ? null
            : Math.max(
                  0,
                  Math.min(selectedSpanIndex ?? 0, track.spans.length - 1),
              );
    const spans = buildRepeatingEditor({
        key: "audio-track-spans",
        label: "Spans",
        sectionClass: "vst-audio-track-spans",
        items: track.spans.map((_, spanIndex) => ({
            label: `S${spanIndex + 1}`,
            focusKey: `audio-track-${trackIndex}-span-tab-${spanIndex}`,
            title: `Edit span ${spanIndex + 1}`,
            active: spanIndex === activeSpanIndex,
            className: "vst-audio-track-span-tab",
            onSelect: () =>
                setSelection({
                    kind: "audio-track-span",
                    trackIdx: trackIndex,
                    spanIdx: spanIndex,
                }),
            onDelete: () => {
                commitTrack(ctx, trackId, (next) => {
                    next.spans.splice(spanIndex, 1);
                });
                if (track.spans.length) {
                    setSelection({
                        kind: "audio-track-span",
                        trackIdx: trackIndex,
                        spanIdx: Math.min(spanIndex, track.spans.length - 1),
                    });
                } else {
                    setSelection({ kind: "audio-track", trackIdx: trackIndex });
                }
                ctx.render();
            },
        })),
        add: {
            title: "Add a span to this audio track",
            className: "vst-audio-track-add-span",
            onClick: () => {
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
                setSelection({
                    kind: "audio-track-span",
                    trackIdx: trackIndex,
                    spanIdx: track.spans.length - 1,
                });
                ctx.render();
            },
        },
        remove: {
            title:
                activeSpanIndex === null
                    ? "No span to delete"
                    : `Delete span ${activeSpanIndex + 1}`,
            className: "vst-audio-track-delete-span",
        },
        editor:
            activeSpanIndex === null
                ? undefined
                : buildSpanEditor(
                      ctx,
                      state,
                      track,
                      track.spans[activeSpanIndex],
                  ),
    });
    fields.appendChild(spans.section);
    return fields;
};

export const buildAudioTracksPanel = (
    ctx: DetailStripContext,
    state: VideoStagesConfig,
    selection: Extract<
        TimelineSelection,
        { kind: "none" | "audio-track" | "audio-track-span" }
    > = { kind: "none" },
): HTMLElement => {
    const tracks = state.audioTracks ?? [];
    const activeTrackIndex =
        tracks.length === 0
            ? null
            : Math.max(
                  0,
                  Math.min(
                      selection.kind === "audio-track" ||
                          selection.kind === "audio-track-span"
                          ? selection.trackIdx
                          : 0,
                      tracks.length - 1,
                  ),
              );
    const selectedSpanIndex =
        selection.kind === "audio-track-span" &&
        selection.trackIdx === activeTrackIndex
            ? selection.spanIdx
            : null;
    const built = buildRepeatingEditor({
        key: "audio-tracks",
        label: "Planned multi-clip audio",
        sectionClass: "vst-audio-tracks-panel",
        items: tracks.map((_, trackIndex) => ({
            label: `T${trackIndex + 1}`,
            focusKey: `audio-track-tab-${trackIndex}`,
            title: `Edit audio track ${trackIndex + 1}`,
            active: trackIndex === activeTrackIndex,
            className: "vst-audio-track-tab",
            onSelect: () =>
                setSelection({ kind: "audio-track", trackIdx: trackIndex }),
            onDelete: () => {
                ctx.commitState((next) => {
                    next.audioTracks?.splice(trackIndex, 1);
                });
                if (tracks.length) {
                    setSelection({
                        kind: "audio-track",
                        trackIdx: Math.min(trackIndex, tracks.length - 1),
                    });
                } else {
                    setSelection({ kind: "none" });
                }
                ctx.render();
            },
        })),
        add: {
            title: "Add a planned multi-clip audio track",
            className: "vst-audio-track-add",
            onClick: () => {
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
                setSelection({
                    kind: "audio-track",
                    trackIdx: tracks.length - 1,
                });
                ctx.render();
            },
        },
        remove: {
            title:
                activeTrackIndex === null
                    ? "No audio track to delete"
                    : `Delete audio track ${activeTrackIndex + 1}`,
            className: "vst-audio-track-delete",
        },
        editor:
            activeTrackIndex === null
                ? undefined
                : buildTrackEditor(
                      ctx,
                      state,
                      tracks[activeTrackIndex],
                      activeTrackIndex,
                      selectedSpanIndex,
                  ),
    });
    const warning = document.createElement("div");
    warning.className = "vst-audio-tracks-planned-warning";
    warning.textContent = "Planned — runtime mixer not yet connected";
    built.list.before(warning);
    return built.section;
};
