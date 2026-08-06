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
    buildStackSection,
    clampStartLength,
} from "../detailWidgets";
import { initVideoFromProbe, probeInitVideo } from "../initVideoProbe";
import { getTimelineStore } from "../persistence/repository";
import { applyClipDurationResize } from "../timelineEdit";
import type { Clip } from "../types";
import type { DetailStripContext } from "./context";

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
                () => defaults,
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

    const hint = document.createElement("small");
    hint.className = "vst-detail-field-hint";
    hint.textContent =
        "Use an existing video file as this clip instead of generating it.";
    col.appendChild(hint);
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
    if (!source) {
        return wrap;
    }

    const info = document.createElement("small");
    info.className = "vst-detail-field-hint";
    info.textContent = `Detected: ${
        source.fps > 0 ? `${source.fps} fps` : "unknown fps"
    } · ${
        source.durationSeconds > 0
            ? `${source.durationSeconds.toFixed(1)} s`
            : "unknown length"
    }`;
    col.appendChild(info);

    const fileLimit =
        source.durationSeconds > 0
            ? source.durationSeconds
            : source.startSeconds + source.lengthSeconds;
    const syncClipDuration = (target: Clip): void => {
        const defaults = context.authoring().defaults;
        applyClipDurationResize(
            target,
            Math.max(
                CLIP_DURATION_MIN,
                target.initVideo?.lengthSeconds ?? target.duration,
            ),
            () => defaults,
            getTimelineStore().getState().fps,
        );
    };
    const clampSource = (
        start: number,
        length: number,
    ): { start: number; length: number } =>
        clampStartLength(start, length, fileLimit, CLIP_DURATION_MIN);

    const startInput = context.buildClampedNumber({
        key: "source-start",
        value: source.startSeconds,
        min: 0,
        max: Math.max(0, fileLimit - CLIP_DURATION_MIN),
        step: DURATION_STEP,
        readBack: (clips) => clips[clipIdx]?.initVideo?.startSeconds ?? null,
        mutate: (clips, value) => {
            const target = clips[clipIdx];
            const targetSource = target?.initVideo;
            if (target && targetSource) {
                const next = clampSource(value, targetSource.lengthSeconds);
                targetSource.startSeconds = next.start;
                targetSource.lengthSeconds = next.length;
                syncClipDuration(target);
            }
        },
    });
    col.appendChild(
        buildField(
            "Start (s)",
            startInput,
            undefined,
            "Where inside the source file this clip's footage begins. Trims " +
                "the front of the file.",
        ),
    );

    const lengthInput = context.buildClampedNumber({
        key: "source-length",
        value: source.lengthSeconds,
        min: CLIP_DURATION_MIN,
        max: fileLimit,
        step: DURATION_STEP,
        readBack: (clips) => clips[clipIdx]?.initVideo?.lengthSeconds ?? null,
        mutate: (clips, value) => {
            const target = clips[clipIdx];
            const targetSource = target?.initVideo;
            if (target && targetSource) {
                const next = clampSource(targetSource.startSeconds, value);
                targetSource.startSeconds = next.start;
                targetSource.lengthSeconds = next.length;
                syncClipDuration(target);
            }
        },
    });
    col.appendChild(
        buildField(
            "Length (s)",
            lengthInput,
            undefined,
            "How many seconds of the source file this clip uses, starting at " +
                "Start. This also becomes the clip's duration.",
        ),
    );

    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent =
        "This range (conformed to the timeline fps and size) is the clip's " +
        "starting point: the first stage refines it using its Control value, " +
        "later stages refine or upscale it, and a retake regenerates part of it.";
    col.appendChild(note);

    const removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.className =
        "interrupt-button vst-btn-tiny vst-detail-delete vst-detail-rail-btn";
    removeButton.textContent = "Remove source video";
    removeButton.addEventListener("click", (event) => {
        event.preventDefault();
        removeSource();
    });
    col.appendChild(removeButton);
    return wrap;
};
