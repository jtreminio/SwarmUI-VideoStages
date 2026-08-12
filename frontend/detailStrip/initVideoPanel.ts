import { reconcileArchitectureIncomingIcLoraDrives } from "../architectures/behaviorRegistry";
import { reconcileClipArchitectureIdentity } from "../architectures/clipIdentity";
import {
    INIT_VIDEO_PROBE_SLOT,
    runClipMediaProbe,
} from "../clipMediaProbeGuard";
import { CLIP_DURATION_MIN } from "../constants";
import {
    appendHelp,
    buildField,
    buildMediaPickRow,
    buildOptionSelect,
    buildStackSection,
} from "../detailWidgets";
import {
    MEDIA_SOURCE_PREVIOUS_CLIP,
    MEDIA_SOURCE_UPLOAD,
} from "../generatedMediaSource";
import { initVideoFromProbe, probeInitVideo } from "../mediaProbe";
import { getTimelineStore } from "../persistence/repository";
import { applyClipDurationResize } from "../timelineEdit";
import {
    type SourceRange,
    setInPoint,
    setOutPoint,
    sourceLimitSeconds,
    type TrimLimits,
    toInOut,
} from "../trimGeometry";
import type { Clip } from "../types";
import type { DetailStripContext } from "./context";
import { buildSidebarMediaPreview } from "./sidebarMediaPreview";
import { buildTrimLauncher, openTrimModal } from "./trimModal";

const DURATION_STEP = 0.1;

/** Metadata probing resolves asynchronously, so it runs through the probe guard. */
const applyPickedInitVideo = (
    context: DetailStripContext,
    clipId: string,
    data: string,
    fileName: string,
): void =>
    runClipMediaProbe({
        clipId,
        slot: INIT_VIDEO_PROBE_SLOT,
        probe: () => probeInitVideo(data),
        apply: (target, probe, state) => {
            const { capabilities, defaults } = context.authoring();
            target.initVideo = initVideoFromProbe(
                probe,
                data,
                fileName,
                target.duration,
            );
            reconcileClipArchitectureIdentity(target, capabilities.catalog);
            applyClipDurationResize(
                target,
                Math.max(CLIP_DURATION_MIN, target.initVideo.lengthSeconds),
                defaults,
                state.fps,
            );
        },
        onApplied: () => context.render(),
    });

export const buildInitVideoSection = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    open = false,
): HTMLElement => {
    const { wrap, col } = buildStackSection(
        "init-video",
        "Source Video",
        "vst-detail-source-col",
        open,
    );
    const sectionLabel = wrap.querySelector<HTMLElement>(
        ":scope > .input-group-header .header-label",
    );
    if (sectionLabel) {
        appendHelp(
            sectionLabel,
            wrap,
            "Source Video",
            "Start this clip from an existing video instead of generating it. " +
                "Stages then refine/upscale the footage and a retake can " +
                "regenerate part of it.",
        );
    }
    const source = clip.initVideo;
    const removeSource = (): void => {
        context.structuralCommit((clips) => {
            const target = clips[clipIdx];
            if (!target?.initVideo) {
                return null;
            }
            const transaction = context.authoring();
            target.initVideo = null;
            reconcileClipArchitectureIdentity(
                target,
                transaction.capabilities.catalog,
            );
            reconcileArchitectureIncomingIcLoraDrives(
                clips,
                transaction.generatedEntryMode,
                transaction.capabilities.catalog,
            );
            return "render";
        });
    };
    const sourceKind =
        source?.source === MEDIA_SOURCE_PREVIOUS_CLIP
            ? MEDIA_SOURCE_PREVIOUS_CLIP
            : MEDIA_SOURCE_UPLOAD;
    if (clipIdx > 0) {
        const sourceSelect = buildOptionSelect(
            [
                { value: MEDIA_SOURCE_UPLOAD, label: "Upload" },
                {
                    value: MEDIA_SOURCE_PREVIOUS_CLIP,
                    label: "Previous Clip Output",
                },
            ],
            sourceKind,
            (value) => {
                context.structuralCommit((clips) => {
                    const target = clips[clipIdx];
                    const previous = clips[clipIdx - 1];
                    if (!target || !previous) {
                        return null;
                    }
                    if (value === MEDIA_SOURCE_PREVIOUS_CLIP) {
                        const duration = previous.duration;
                        target.initVideo = {
                            source: MEDIA_SOURCE_PREVIOUS_CLIP,
                            data: "",
                            fileName: null,
                            fps: getTimelineStore().getState().fps,
                            durationSeconds: duration,
                            startSeconds: 0,
                            lengthSeconds: duration,
                        };
                        applyClipDurationResize(
                            target,
                            duration,
                            context.authoring().defaults,
                        );
                    } else if (
                        target.initVideo?.source === MEDIA_SOURCE_PREVIOUS_CLIP
                    ) {
                        target.initVideo = null;
                    }
                    reconcileClipArchitectureIdentity(
                        target,
                        context.authoring().capabilities.catalog,
                    );
                    reconcileArchitectureIncomingIcLoraDrives(
                        clips,
                        context.authoring().generatedEntryMode,
                        context.authoring().capabilities.catalog,
                    );
                    return "render";
                });
            },
        );
        sourceSelect.classList.add("vst-init-video-source");
        col.appendChild(buildField("Video source", sourceSelect));
    }

    const hint = document.createElement("small");
    hint.className = "vst-detail-field-hint";
    const sourceIsPrevious = sourceKind === MEDIA_SOURCE_PREVIOUS_CLIP;
    hint.textContent = sourceIsPrevious
        ? "Uses the previous clip's decoded output as this clip's starting footage."
        : "Use an existing video file as this clip instead of generating it.";
    col.appendChild(hint);
    if (!sourceIsPrevious) {
        col.appendChild(
            buildMediaPickRow(
                "Video file",
                "video/*",
                ["video"],
                source?.fileName ?? null,
                (data, fileName) => {
                    if (clip.id) {
                        applyPickedInitVideo(context, clip.id, data, fileName);
                    }
                },
                removeSource,
            ),
        );
    }
    if (!source) {
        return wrap;
    }

    const shown: SourceRange = {
        startSeconds: source.startSeconds,
        lengthSeconds: source.lengthSeconds,
    };
    if (!sourceIsPrevious) {
        col.appendChild(buildSidebarMediaPreview("video", source.data, shown));
    }

    const info = document.createElement("small");
    info.className = "vst-detail-field-hint";
    info.textContent = sourceIsPrevious
        ? `Previous output: ${source.durationSeconds.toFixed(1)} s`
        : `Detected: ${
              source.fps > 0 ? `${source.fps} fps` : "unknown fps"
          } · ${
              source.durationSeconds > 0
                  ? `${source.durationSeconds.toFixed(1)} s`
                  : "unknown length"
          }`;
    col.appendChild(info);

    const fileLimit = sourceLimitSeconds(source);
    const fps = getTimelineStore().getState().fps;
    const limits: TrimLimits = {
        limitSeconds: fileLimit,
        minLengthSeconds: CLIP_DURATION_MIN,
        fps,
    };
    const writeRange = (target: Clip, next: SourceRange): void => {
        const targetSource = target.initVideo;
        if (!targetSource) {
            return;
        }
        targetSource.startSeconds = next.startSeconds;
        targetSource.lengthSeconds = next.lengthSeconds;
        Object.assign(shown, next);
        applyClipDurationResize(
            target,
            Math.max(CLIP_DURATION_MIN, next.lengthSeconds),
            context.authoring().defaults,
            fps,
        );
    };

    const edgeInput = (
        edge: "in" | "out",
        apply: (
            current: SourceRange,
            value: number,
            trimLimits: TrimLimits,
        ) => SourceRange,
    ): HTMLInputElement =>
        context.buildClampedNumber({
            key: `source-${edge}`,
            value: toInOut(shown)[edge === "in" ? "inSeconds" : "outSeconds"],
            min: edge === "in" ? 0 : CLIP_DURATION_MIN,
            max: fileLimit,
            step: DURATION_STEP,
            readBack: (clips) => {
                const readSource = clips[clipIdx]?.initVideo;
                return readSource
                    ? toInOut(readSource)[
                          edge === "in" ? "inSeconds" : "outSeconds"
                      ]
                    : null;
            },
            mutate: (clips, value) => {
                const target = clips[clipIdx];
                if (target?.initVideo) {
                    writeRange(target, apply(target.initVideo, value, limits));
                }
            },
        });

    col.appendChild(
        buildField(
            "In (s)",
            edgeInput("in", setInPoint),
            undefined,
            "Where inside the source file this clip's footage begins. Moving " +
                "it leaves Out where it is, so the clip gets shorter.",
        ),
    );
    col.appendChild(
        buildField(
            "Out (s)",
            edgeInput("out", setOutPoint),
            undefined,
            "Where inside the source file this clip's footage ends. This also " +
                "becomes the clip's duration.",
        ),
    );

    if (!sourceIsPrevious && source.durationSeconds > 0) {
        const range = toInOut(shown);
        col.appendChild(
            buildTrimLauncher(
                `Range ${range.inSeconds.toFixed(1)}–${range.outSeconds.toFixed(1)} s` +
                    ` · Uses ${shown.lengthSeconds.toFixed(1)} s of ${fileLimit.toFixed(1)} s`,
                () =>
                    openTrimModal({
                        mediaKind: "video",
                        title: "Trim Source Video",
                        fileName: source.fileName ?? "Source video",
                        dataUri: source.data,
                        range: shown,
                        limits,
                        impactText: (next) =>
                            `Clip duration becomes ${next.lengthSeconds.toFixed(1)} s`,
                        onApply: (next) => {
                            context.commit((clips) => {
                                const target = clips[clipIdx];
                                if (target) {
                                    writeRange(target, next);
                                }
                            });
                            context.render();
                        },
                    }),
            ),
        );
    }

    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent =
        "This range (conformed to the timeline fps and size) is the clip's " +
        "starting point: the first stage refines it using its Control value, " +
        "later stages refine or upscale it, and a retake regenerates part of it.";
    col.appendChild(note);

    return wrap;
};
