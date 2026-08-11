import {
    AUDIO_SOURCE_UPLOAD,
    buildAudioTrackSourceOptions,
    isAceStepFunAudioSource,
} from "../audioSource";
import {
    AUDIO_SPAN_DEFAULT_LENGTH,
    AUDIO_SPAN_MIN_LENGTH,
    AUDIO_SPAN_STEP,
    AUDIO_SPAN_VOLUME_DEFAULT,
    AUDIO_SPAN_VOLUME_MAX,
    AUDIO_SPAN_VOLUME_MIN,
    AUDIO_SPAN_VOLUME_SLIDER_MAX,
    AUDIO_SPAN_VOLUME_SLIDER_MIN,
    AUDIO_SPAN_VOLUME_SLIDER_STEP,
} from "../constants";
import {
    buildField,
    buildMediaPickRow,
    buildNumber,
    buildOptionSelect,
    buildRepeatingEditor,
    buildSlider,
} from "../detailWidgets";
import type { ClipTimelineWindow } from "../documentQueries";
import { createEntityId } from "../identity";
import { probeMediaDurationSeconds } from "../mediaProbe";
import { selectionAfterRemoval, setSelection } from "../selection";
import { clampStartLength, toInOut } from "../trimGeometry";
import type {
    AudioTrack,
    AudioTrackSpan,
    AuthoringDocument,
    TimelineSelection,
} from "../types";
import { roundToTenth } from "../utils";
import type { DetailStripContext } from "./context";
import { buildSidebarMediaPreview } from "./sidebarMediaPreview";
import { buildTrimLauncher, openTrimModal } from "./trimModal";

const timelineDuration = (state: AuthoringDocument): number =>
    state.clips.reduce((sum, clip) => sum + Math.max(0, clip.duration || 0), 0);

const primarySpan = (track: AudioTrack): AudioTrackSpan | null =>
    track.spans[0] ?? null;

const commitTrack = (
    ctx: DetailStripContext,
    trackId: string,
    mutate: (track: AudioTrack, span: AudioTrackSpan) => void,
    debounceKey?: string,
): void => {
    const apply = (state: AuthoringDocument): void => {
        const track = state.audioTracks?.find((entry) => entry.id === trackId);
        const span = track ? primarySpan(track) : null;
        if (track && span) {
            mutate(track, span);
        }
    };
    if (debounceKey) {
        ctx.debouncedCommitState(debounceKey, apply);
    } else {
        ctx.commitState(apply);
    }
};

const buildTrackEditor = (
    ctx: DetailStripContext,
    state: AuthoringDocument,
    track: AudioTrack,
    trackIndex: number,
): HTMLElement => {
    const trackId = track.id as string;
    const span = primarySpan(track);
    const fields = document.createElement("div");
    fields.className =
        "vst-detail-col vst-detail-instance-fields vst-audio-track";
    fields.dataset.vstAudioTrackId = trackId;
    if (!span) {
        const note = document.createElement("p");
        note.className = "vst-detail-note";
        note.textContent = "This audio track has no timeline window.";
        fields.appendChild(note);
        return fields;
    }

    const total = Math.max(AUDIO_SPAN_MIN_LENGTH, timelineDuration(state));
    const clamped = (): { start: number; length: number } =>
        clampStartLength(
            span.timelineStartSeconds ?? 0,
            span.timelineLengthSeconds ?? AUDIO_SPAN_DEFAULT_LENGTH,
            total,
            AUDIO_SPAN_MIN_LENGTH,
        );
    const aceReference =
        track.source.kind === "AceStepFun" ? track.source.reference : "";
    const sourceSelect = buildOptionSelect(
        buildAudioTrackSourceOptions(aceReference),
        aceReference || AUDIO_SOURCE_UPLOAD,
        (value) => {
            commitTrack(ctx, trackId, (next) => {
                if (isAceStepFunAudioSource(value)) {
                    next.source.kind = "AceStepFun";
                    next.source.reference = value;
                    next.source.uploadedAudio = null;
                    next.source.mediaDurationSeconds = 0;
                } else {
                    next.source.kind = "Upload";
                    next.source.reference =
                        next.source.uploadedAudio?.fileName ?? "";
                }
            });
            ctx.render();
        },
    );
    fields.appendChild(
        buildField(
            "Source",
            sourceSelect,
            undefined,
            "Where this timeline-wide overlay comes from. It is cut at clip " +
                "boundaries during generation and mixed additively.",
        ),
    );

    if (!aceReference) {
        fields.appendChild(
            buildMediaPickRow(
                "Audio Upload",
                "audio/*",
                ["audio"],
                track.source.uploadedAudio?.fileName,
                (data, fileName) => {
                    commitTrack(ctx, trackId, (next) => {
                        next.source.kind = "Upload";
                        next.source.reference = fileName ?? "";
                        next.source.uploadedAudio = { data, fileName };
                        next.source.mediaDurationSeconds = 0;
                    });
                    ctx.render();
                    void probeMediaDurationSeconds(data).then((duration) => {
                        commitTrack(ctx, trackId, (next) => {
                            if (next.source.uploadedAudio?.data !== data) {
                                return;
                            }
                            const seconds = roundToTenth(duration);
                            if (seconds > 0) {
                                next.source.mediaDurationSeconds = seconds;
                            }
                        });
                        ctx.render();
                    });
                },
                () => {
                    commitTrack(ctx, trackId, (next) => {
                        next.source.reference = "";
                        next.source.uploadedAudio = null;
                        next.source.mediaDurationSeconds = 0;
                    });
                    ctx.render();
                },
            ),
        );
    }

    const volume = track.volume ?? AUDIO_SPAN_VOLUME_DEFAULT;
    const volumeSlider = buildSlider(
        "Volume",
        volume,
        AUDIO_SPAN_VOLUME_MIN,
        AUDIO_SPAN_VOLUME_MAX,
        AUDIO_SPAN_VOLUME_SLIDER_STEP,
        (value) => {
            commitTrack(
                ctx,
                trackId,
                (next) => {
                    next.volume = Math.min(
                        AUDIO_SPAN_VOLUME_MAX,
                        Math.max(AUDIO_SPAN_VOLUME_MIN, value),
                    );
                },
                `audio-track-${trackId}-volume`,
            );
        },
        {
            sliderMin: AUDIO_SPAN_VOLUME_SLIDER_MIN,
            sliderMax: AUDIO_SPAN_VOLUME_SLIDER_MAX,
            numberStep: "any",
        },
    );
    volumeSlider
        .querySelector<HTMLInputElement>("input.auto-slider-number")
        ?.setAttribute("data-vst-focus-key", `audio-track-${trackId}-volume`);
    fields.appendChild(volumeSlider);

    const geometry = clamped();
    const shown = {
        startSeconds: span.sourceStartSeconds,
        lengthSeconds: geometry.length,
    };
    const startInput = buildNumber(
        geometry.start,
        0,
        Math.max(0, total - AUDIO_SPAN_MIN_LENGTH),
        AUDIO_SPAN_STEP,
        (value) => {
            commitTrack(
                ctx,
                trackId,
                (_next, nextSpan) => {
                    const next = clampStartLength(
                        value,
                        nextSpan.timelineLengthSeconds ??
                            AUDIO_SPAN_DEFAULT_LENGTH,
                        total,
                        AUDIO_SPAN_MIN_LENGTH,
                    );
                    nextSpan.timelineStartSeconds = next.start;
                    nextSpan.timelineLengthSeconds = next.length;
                    shown.lengthSeconds = next.length;
                },
                `audio-track-${trackId}-start`,
            );
        },
    );
    startInput.setAttribute(
        "data-vst-focus-key",
        `audio-track-${trackId}-start`,
    );
    fields.appendChild(
        buildField(
            "Timeline start (s)",
            startInput,
            undefined,
            "Seconds from the beginning of the complete multi-clip timeline.",
        ),
    );

    const trimInput = buildNumber(
        span.sourceStartSeconds,
        0,
        Number.MAX_SAFE_INTEGER,
        AUDIO_SPAN_STEP,
        (value) => {
            commitTrack(
                ctx,
                trackId,
                (_next, nextSpan) => {
                    shown.startSeconds = Math.max(0, value);
                    nextSpan.sourceStartSeconds = shown.startSeconds;
                },
                `audio-track-${trackId}-trim`,
            );
        },
    );
    trimInput.setAttribute("data-vst-focus-key", `audio-track-${trackId}-trim`);
    fields.appendChild(
        buildField(
            "Trim start (s)",
            trimInput,
            undefined,
            "Skip this many seconds from the source before playback begins.",
        ),
    );

    const lengthInput = buildNumber(
        geometry.length,
        AUDIO_SPAN_MIN_LENGTH,
        total,
        AUDIO_SPAN_STEP,
        (value) => {
            commitTrack(
                ctx,
                trackId,
                (_next, nextSpan) => {
                    const next = clampStartLength(
                        nextSpan.timelineStartSeconds ?? 0,
                        value,
                        total,
                        AUDIO_SPAN_MIN_LENGTH,
                    );
                    nextSpan.timelineStartSeconds = next.start;
                    nextSpan.timelineLengthSeconds = next.length;
                    shown.lengthSeconds = next.length;
                },
                `audio-track-${trackId}-length`,
            );
        },
    );
    lengthInput.setAttribute(
        "data-vst-focus-key",
        `audio-track-${trackId}-length`,
    );
    fields.appendChild(
        buildField(
            "Length (s)",
            lengthInput,
            undefined,
            "How long this track plays across the complete timeline.",
        ),
    );

    const uploadedAudio = track.source.uploadedAudio;
    const durationSeconds = track.source.mediaDurationSeconds ?? 0;
    if (uploadedAudio) {
        fields.appendChild(
            buildSidebarMediaPreview("audio", uploadedAudio.data, shown),
        );
        const limitSeconds = Math.max(
            durationSeconds,
            shown.startSeconds + shown.lengthSeconds,
        );
        const limits = {
            limitSeconds,
            minLengthSeconds: AUDIO_SPAN_MIN_LENGTH,
            fps: 0,
        };
        const range = toInOut(shown);
        const maxTimelineLength = total - geometry.start;
        fields.appendChild(
            buildTrimLauncher(
                `Range ${range.inSeconds.toFixed(1)}–${range.outSeconds.toFixed(1)} s` +
                    ` · Plays ${shown.lengthSeconds.toFixed(1)} s of ${limitSeconds.toFixed(1)} s`,
                () =>
                    openTrimModal({
                        mediaKind: "audio",
                        title: "Trim Audio Track",
                        fileName: uploadedAudio.fileName ?? "Audio track",
                        dataUri: uploadedAudio.data,
                        range: shown,
                        limits,
                        impactText: (next) =>
                            `Track length becomes ${Math.min(next.lengthSeconds, maxTimelineLength).toFixed(1)} s`,
                        onApply: (next) => {
                            commitTrack(ctx, trackId, (_next, nextSpan) => {
                                nextSpan.sourceStartSeconds = next.startSeconds;
                                nextSpan.timelineLengthSeconds = Math.min(
                                    next.lengthSeconds,
                                    maxTimelineLength,
                                );
                            });
                            ctx.render();
                        },
                    }),
            ),
        );
    }

    fields.dataset.vstTrackIndex = `${trackIndex}`;
    return fields;
};

const addAudioTrack = (
    ctx: DetailStripContext,
    state: AuthoringDocument,
    clipWindow?: ClipTimelineWindow,
): number => {
    const total = Math.max(AUDIO_SPAN_MIN_LENGTH, timelineDuration(state));
    const start = Math.min(
        Math.max(0, clipWindow?.startSeconds ?? 0),
        Math.max(0, total - AUDIO_SPAN_MIN_LENGTH),
    );
    const availableLength = Math.max(
        AUDIO_SPAN_MIN_LENGTH,
        Math.min(
            total - start,
            clipWindow
                ? clipWindow.endSeconds - clipWindow.startSeconds
                : total,
        ),
    );
    const nextIndex = state.audioTracks?.length ?? 0;
    ctx.commitState((next) => {
        next.audioTracks ??= [];
        next.audioTracks.push({
            id: createEntityId("audio_track"),
            source: {
                kind: "Upload",
                reference: "",
                uploadedAudio: null,
                mediaDurationSeconds: 0,
            },
            volume: AUDIO_SPAN_VOLUME_DEFAULT,
            spans: [
                {
                    id: createEntityId("audio_span"),
                    timelineStartSeconds: start,
                    timelineLengthSeconds: Math.min(
                        AUDIO_SPAN_DEFAULT_LENGTH,
                        availableLength,
                    ),
                    sourceStartSeconds: 0,
                },
            ],
        });
    });
    return nextIndex;
};

export interface AudioTracksPanelOptions {
    trackIndices?: readonly number[];
    clipWindow?: ClipTimelineWindow;
}

export const buildAudioTracksPanel = (
    ctx: DetailStripContext,
    state: AuthoringDocument,
    selection: Extract<TimelineSelection, { kind: "none" | "audio-track" }> = {
        kind: "none",
    },
    options?: AudioTracksPanelOptions,
): HTMLElement => {
    const tracks = state.audioTracks ?? [];
    const visibleTrackIndices = options?.trackIndices
        ? options.trackIndices.filter(
              (trackIndex) => trackIndex >= 0 && trackIndex < tracks.length,
          )
        : tracks.map((_, trackIndex) => trackIndex);
    const selectedTrackIndex =
        selection.kind === "audio-track" ? selection.trackIdx : null;
    const activeTrackIndex =
        selectedTrackIndex !== null &&
        visibleTrackIndices.includes(selectedTrackIndex)
            ? selectedTrackIndex
            : (visibleTrackIndices[0] ?? null);
    const built = buildRepeatingEditor({
        key: "audio-tracks",
        label: "Audio Tracks",
        sectionClass: "vst-audio-tracks-panel",
        open: selectedTrackIndex !== null,
        items: visibleTrackIndices.map((trackIndex) => ({
            label: `A${trackIndex + 1}`,
            stateKey: `audio-track:${tracks[trackIndex].id}`,
            focusKey: `audio-track-tab-${trackIndex}`,
            title: `Edit audio track A${trackIndex + 1}`,
            active: trackIndex === activeTrackIndex,
            className: "vst-audio-track-tab",
            onSelect: () =>
                setSelection({ kind: "audio-track", trackIdx: trackIndex }),
            onDelete: () => {
                ctx.commitState((next) => {
                    next.audioTracks?.splice(trackIndex, 1);
                });
                setSelection(
                    selectionAfterRemoval(
                        trackIndex,
                        tracks.length - 1,
                        (index) => ({ kind: "audio-track", trackIdx: index }),
                        { kind: "none" },
                    ),
                );
                ctx.render();
            },
        })),
        editorForItem: (itemIndex) => {
            const trackIndex = visibleTrackIndices[itemIndex];
            const track = tracks[trackIndex];
            return track
                ? buildTrackEditor(ctx, state, track, trackIndex)
                : undefined;
        },
        add: {
            title: "Add an audio track spanning the timeline",
            label: "+ Add Audio Track",
            className: "vst-audio-track-add",
            onClick: () => {
                const trackIdx = addAudioTrack(ctx, state, options?.clipWindow);
                setSelection({ kind: "audio-track", trackIdx });
                ctx.render();
            },
        },
        remove: {
            title:
                activeTrackIndex === null
                    ? "No audio track to delete"
                    : `Delete audio track A${activeTrackIndex + 1}`,
            className: "vst-audio-track-delete",
        },
    });
    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent =
        tracks.length === 0
            ? "No audio tracks."
            : visibleTrackIndices.length === 0
              ? "No audio tracks in this clip."
              : "Audio tracks are cut per clip during generation; overlapping tracks mix together.";
    built.content.insertBefore(note, built.content.firstChild);
    return built.section;
};

export const buildTimelineAudioTracksBody = (
    ctx: DetailStripContext,
    state: AuthoringDocument,
    selection: Extract<TimelineSelection, { kind: "audio-track" }>,
): HTMLElement => {
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-audio-body";
    body.appendChild(buildAudioTracksPanel(ctx, state, selection));
    return body;
};
