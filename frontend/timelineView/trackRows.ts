import type { CapabilityViewResolver } from "../architectures/policy";
import { isAceStepFunAudioSource } from "../audioSource";
import { mediaPreviewSrc } from "../constants";
import {
    audioSourceBadge,
    escapeHtml,
    formatTimeLabel,
    keyframeTimeSeconds,
    refSourceLabel,
    type TimelineUnit,
    truncate,
} from "../timelineDetail";
import { keyframeLeftPercent, spanGeometry } from "../trackDomUtils";
import type { AudioTrack, Clip, PromptWindow, RefImage } from "../types";
import { roundToTenth } from "../utils";
import {
    audioSegmentWaveBarHeights,
    type RegionLayout,
    waveBarHeights,
} from "./layout";
import {
    backgroundImageDataAttr,
    clipInnerWidth,
    headTag,
    renderTrackHead,
    renderWindowSpan,
} from "./rendering";

export interface PromptWindowGeom {
    startSec: number;
    endSec: number;
    leftPx: number;
    widthPx: number;
    active: boolean;
}

const promptWindowGeom = (
    layout: RegionLayout,
    window: PromptWindow,
    pxPerSecond: number,
): PromptWindowGeom => {
    const geometry = spanGeometry(
        window.start,
        window.duration,
        layout.durationSeconds,
        { unit: "px", pxPerSecond, minWidth: 2 },
    );
    return {
        startSec: geometry.startSeconds,
        endSec: geometry.endSeconds,
        leftPx: geometry.left,
        widthPx: geometry.width,
        active: `${window.prompt ?? ""}`.trim() !== "",
    };
};

const PROMPT_PLACEHOLDER = "(no prompt)";

export const renderPromptTrackRow = (
    clips: Clip[],
    layouts: RegionLayout[],
    pxPerSecond: number,
    globalPrompt: string,
    capabilities?: CapabilityViewResolver,
): string => {
    const globalTrimmed = `${globalPrompt ?? ""}`.trim();
    const parts: string[] = [];
    for (let i = 0; i < layouts.length; i++) {
        const layout = layouts[i];
        const clip = clips[i];
        if (!clip) {
            continue;
        }
        const width = clipInnerWidth(layout.widthPx);
        const windows = clip.promptWindows ?? [];
        const clipCapabilities = capabilities?.forClip(clip);
        const majorSupported =
            clipCapabilities?.decision("majorPrompt").supported ?? true;
        const relaySupported =
            clipCapabilities?.decision("promptRelay").supported ?? true;
        const ownPrompt = `${clip.prompt ?? ""}`.trim();
        const inherited = ownPrompt === "";
        const major = inherited ? globalTrimmed : ownPrompt;
        const overlays = windows
            .map((window) => promptWindowGeom(layout, window, pxPerSecond))
            .filter(
                (geometry) =>
                    geometry.active && geometry.endSec > geometry.startSec,
            )
            .map(
                (geometry) =>
                    `<div class="vst-major-off" style="left:${geometry.leftPx}px;width:${geometry.widthPx}px" aria-hidden="true"></div>`,
            )
            .join("");
        const majorText =
            major === "" ? PROMPT_PLACEHOLDER : truncate(major, 120);
        const majorClass =
            (major === "" ? " vst-major-empty" : "") +
            (inherited && major !== "" ? " vst-major-inherited" : "");
        const majorTitle =
            (major === "" ? PROMPT_PLACEHOLDER : major) +
            (inherited && major !== ""
                ? " — inherited from the global prompt; click to set a clip prompt"
                : " — click to edit");
        if (majorSupported || ownPrompt !== "") {
            parts.push(
                `<div class="vst-major-seg${majorClass}${majorSupported ? "" : " vst-capability-disabled"}" data-vst-prompt="major" data-clip-idx="${i}" style="left:${layout.startPx}px;width:${width}px" title="${escapeHtml(majorSupported ? majorTitle : `${majorTitle} — unsupported by this architecture`)}">` +
                    overlays +
                    `<span class="vst-major-text">${escapeHtml(majorText)}</span>` +
                    `</div>`,
            );
        }

        const minorSegments = windows
            .map((window, windowIdx) => {
                const geometry = promptWindowGeom(layout, window, pxPerSecond);
                const text = `${window.prompt ?? ""}`.trim();
                const label = text === "" ? "(empty)" : truncate(text, 60);
                return (
                    `<div class="vst-minor-seg" data-vst-prompt="minor" data-clip-idx="${i}" data-window-idx="${windowIdx}" style="left:${geometry.leftPx}px;width:${geometry.widthPx}px" title="${escapeHtml(`${text || "(empty minor prompt)"} · Shift+click to delete`)}">` +
                    `<span class="vst-minor-resize vst-minor-resize-l" data-vst-minor-edge="left" aria-hidden="true"></span>` +
                    `<span class="vst-minor-text">${escapeHtml(label)}</span>` +
                    `<span class="vst-minor-resize vst-minor-resize-r" data-vst-minor-edge="right" aria-hidden="true"></span>` +
                    `</div>`
                );
            })
            .join("");
        if (relaySupported || windows.length > 0) {
            parts.push(
                `<div class="vst-minor-lane${relaySupported ? "" : " vst-capability-disabled"}"${relaySupported ? " data-vst-prompt-add" : ""} data-clip-idx="${i}" style="left:${layout.startPx}px;width:${width}px" title="${relaySupported ? "Click empty space to add a minor prompt" : "Relay prompts are unsupported; existing windows can be removed"}">${minorSegments}</div>`,
            );
        }
    }
    return (
        `<div class="vst-track-row vst-track-prompt">` +
        renderTrackHead(
            "vst-track-icon-prompt",
            "✎",
            "Prompt",
            headTag("major", "Major", { active: true }) +
                headTag("relay", "Relay", {
                    active: clips.some(
                        (clip) => (clip.promptWindows?.length ?? 0) > 0,
                    ),
                }),
        ) +
        `<div class="vst-track-cell vst-prompt-cell">${parts.join("")}</div>` +
        `</div>`
    );
};

const audioFlagChips = (clip: Clip): string => {
    const chips: string[] = [];
    if (clip.reuseAudio === true) {
        chips.push(
            `<span class="vst-audio-flag" title="Capture audio after the second active stage and reuse it from the third active stage onward">↻</span>`,
        );
    }
    if (clip.clipLengthFromAudio === true) {
        chips.push(
            `<span class="vst-audio-flag" title="Clip length follows the audio length">⇥</span>`,
        );
    }
    if (clip.saveAudioTrack === true) {
        chips.push(
            `<span class="vst-audio-flag" title="Save a standalone MP3 for this clip's audio">MP3</span>`,
        );
    }
    return chips.length === 0
        ? ""
        : `<span class="vst-audio-flags" aria-hidden="true">${chips.join("")}</span>`;
};

const renderTimelineAudioSegmentBlock = (
    track: AudioTrack,
    trackIdx: number,
    totalSeconds: number,
): string => {
    const span = track.spans[0];
    if (
        !span ||
        span.timelineStartSeconds === null ||
        span.timelineLengthSeconds === null
    ) {
        return "";
    }
    const { startSeconds: start, endSeconds: end } = spanGeometry(
        span.timelineStartSeconds,
        span.timelineLengthSeconds,
        totalSeconds,
    );
    const labelText =
        track.source.reference ||
        track.source.uploadedAudio?.fileName ||
        "audio segment";
    const rangeLabel = `${roundToTenth(start)}–${roundToTenth(end)} s`;
    const waveform = audioSegmentWaveBarHeights(trackIdx, trackIdx, 40)
        .map((height) => `<span style="height:${height}%"></span>`)
        .join("");
    return renderWindowSpan({
        className: "vst-audio-seg",
        extraClassName: `vst-audio-seg-tone-${trackIdx % 5}`,
        dataAttrs: `data-vst-audio-seg data-track-idx="${trackIdx}"`,
        edgeAttr: "data-vst-audio-seg-edge",
        labelClass: "vst-audio-label",
        label: labelText,
        title: `${labelText} · ${rangeLabel} · drag to move/resize · Shift+click to delete`,
        ariaLabel: `Edit timeline audio segment ${trackIdx}`,
        startSeconds: start,
        lengthSeconds: end - start,
        durationSeconds: totalSeconds,
        decoration: `<span class="vst-audio-seg-wave" aria-hidden="true">${waveform}</span>`,
    });
};

const renderTimelineAudioSegmentLanes = (
    tracks: AudioTrack[],
    totalSeconds: number,
    totalWidthPx: number,
): string => {
    const place = (laneIdx: number): string =>
        `left:0;width:${totalWidthPx}px;--vst-audio-lane-idx:${laneIdx}`;
    const lanes = tracks.map(
        (track, trackIdx) =>
            `<div class="vst-audio-seg-lane" data-track-idx="${trackIdx}" style="${place(trackIdx)}">` +
            renderTimelineAudioSegmentBlock(track, trackIdx, totalSeconds) +
            `</div>`,
    );
    lanes.push(
        `<div class="vst-audio-seg-lane vst-audio-seg-lane-blank" data-vst-audio-seg-add ` +
            `style="${place(tracks.length)}" title="Click or drag to add a timeline-wide audio segment"></div>`,
    );
    return lanes.join("");
};

export const renderAudioTrackRow = (
    clips: Clip[],
    layouts: RegionLayout[],
    capabilities?: CapabilityViewResolver,
    audioTracks: AudioTrack[] = [],
    pxPerSecond = 1,
    timelineTotalSeconds?: number,
): string => {
    const baseSegments = layouts
        .map((layout) => {
            const clip = clips[layout.index];
            if (!clip) {
                return "";
            }
            const badge = audioSourceBadge(clip.audioSource ?? "");
            const clipCapabilities = capabilities?.forClip(clip);
            const clipAudioSupported =
                clipCapabilities?.decision("clipAudio").supported ?? true;
            const persistedAudio =
                clip.audioSource !== "Native" ||
                clip.uploadedAudio !== null ||
                clip.reuseAudio ||
                clip.clipLengthFromAudio ||
                clip.saveAudioTrack;
            const native = badge.label === "Native";
            const width = clipInnerWidth(layout.widthPx);
            const kindClass = native
                ? " vst-audio-native vst-audio-kind-native"
                : isAceStepFunAudioSource(clip.audioSource ?? "")
                  ? " vst-audio-kind-ace"
                  : " vst-audio-kind-upload";
            const upload =
                !native && clip.audioSource === "Upload"
                    ? clip.uploadedAudio?.fileName
                    : null;
            const labelText = upload
                ? `${badge.label} · ${upload}`
                : badge.label;
            const title = native
                ? "Audio: Native — click to choose an audio source"
                : `${badge.title} — click to edit`;
            const barCount = Math.min(
                400,
                Math.max(8, Math.floor(width / 5.5)),
            );
            const bars = waveBarHeights(layout.index, barCount)
                .map((height) => `<span style="height:${height}%"></span>`)
                .join("");
            const hint = native
                ? `<span class="vst-audio-hint" aria-hidden="true">click to add audio</span>`
                : "";
            const body = `<div class="vst-audio-wave" aria-hidden="true">${bars}</div>${hint}`;
            return (
                `<div class="vst-audio-clip${kindClass}${clipAudioSupported ? "" : " vst-capability-disabled"}"${clipAudioSupported || persistedAudio ? ' data-vst-audio="clip"' : ""} data-clip-idx="${layout.index}" role="button" tabindex="0" style="left:${layout.startPx}px;width:${width}px" title="${escapeHtml(clipAudioSupported ? title : "Clip audio is unsupported; click persisted audio to remove it")}" aria-label="Edit audio for clip ${layout.index}">` +
                `<span class="vst-audio-label">${escapeHtml(labelText)}</span>` +
                audioFlagChips(clip) +
                body +
                `</div>`
            );
        })
        .join("");
    const totalSeconds =
        timelineTotalSeconds ??
        layouts.reduce(
            (max, layout) =>
                Math.max(
                    max,
                    layout.startSeconds + layout.timelineDurationSeconds,
                ),
            0,
        );
    const totalWidthPx = totalSeconds * pxPerSecond;
    const overlaySegments = renderTimelineAudioSegmentLanes(
        audioTracks,
        totalSeconds,
        totalWidthPx,
    );
    const laneCount = Math.max(1, audioTracks.length + 1);
    const laneTags = [headTag("src", "Clip", { active: true })];
    for (let i = 0; i < laneCount; i++) {
        const blank = i === laneCount - 1;
        laneTags.push(
            headTag("seg", blank ? "+" : `S${i}`, {
                active: !blank,
                muted: blank,
                style: `--vst-audio-lane-idx:${i}`,
            }),
        );
    }
    return (
        `<div class="vst-track-row vst-track-audio" style="--vst-audio-lane-count:${laneCount}">` +
        renderTrackHead(
            "vst-track-icon-audio",
            "♪",
            "Audio",
            laneTags.join(""),
        ) +
        `<div class="vst-track-cell vst-audio-cell">${baseSegments}${overlaySegments}</div>` +
        `</div>`
    );
};

const REF_EDGE_ALIGN_FRAMES = 3;

export const renderReferencesTrackRow = (
    clips: Clip[],
    layouts: RegionLayout[],
    fps: number,
    unit: TimelineUnit,
    capabilities?: CapabilityViewResolver,
): string => {
    const lanes = layouts
        .map((layout) => {
            const clip = clips[layout.index];
            if (!clip) {
                return "";
            }
            const width = clipInnerWidth(layout.widthPx);
            const refsSupported =
                capabilities?.forClip(clip).decision("frameReferences")
                    .supported ?? true;
            const marks = (clip.refs ?? [])
                .map((ref: RefImage, refIdx: number) => {
                    const isEnd = ref.fromEnd === true;
                    const frame = Math.max(0, ref.frame ?? 0);
                    const isPrimary = frame === 1 && !isEnd;
                    const time = keyframeTimeSeconds(
                        ref.frame,
                        isEnd,
                        layout.durationSeconds,
                        fps,
                    );
                    const left = keyframeLeftPercent(
                        time,
                        layout.durationSeconds,
                    );
                    const source = refSourceLabel(ref.source ?? "");
                    const image = ref.uploadedImage?.data;
                    const thumbnailData = image
                        ? backgroundImageDataAttr(mediaPreviewSrc(image))
                        : "";
                    const frameLabel = `R ${isEnd ? "-" : ""}${frame}`;
                    const thumbnailClass = `vst-refs-thumb${image ? " vst-refs-has-image" : ""}`;
                    const alignClass =
                        frame > REF_EDGE_ALIGN_FRAMES
                            ? ""
                            : isEnd
                              ? " vst-refs-align-end"
                              : " vst-refs-align-start";
                    const kindClass =
                        (isPrimary ? " vst-refs-primary" : "") +
                        (isEnd ? " vst-refs-fromend" : "") +
                        alignClass;
                    const title =
                        `${source}${isPrimary ? " · cover frame" : ""}${isEnd ? " · from end" : ""}` +
                        ` · frame ${frame} · ${formatTimeLabel(time, unit, fps)}` +
                        ` · click to edit, drag to move · Shift+click to delete`;
                    const label = `Edit reference ${refIdx} (${source}${isEnd ? ", from end" : ""})`;
                    return (
                        `<div class="vst-refs-mark${kindClass}" data-vst-ref="thumb" data-clip-idx="${layout.index}" data-ref-idx="${refIdx}" style="left:${left}%" role="button" tabindex="0" title="${escapeHtml(title)}" aria-label="${escapeHtml(label)}">` +
                        `<span class="${thumbnailClass}"${thumbnailData}><span class="vst-refs-ph">${escapeHtml(frameLabel)}</span></span>` +
                        `</div>`
                    );
                })
                .join("");
            return `<div class="vst-refs-lane${refsSupported ? "" : " vst-capability-disabled"}"${refsSupported ? " data-vst-ref-add" : ""} data-clip-idx="${layout.index}" style="left:${layout.startPx}px;width:${width}px" title="${refsSupported ? "Click to add a reference image at this frame" : "Frame references are unsupported; existing references can be removed"}">${marks}</div>`;
        })
        .join("");
    return (
        `<div class="vst-track-row vst-track-refs">` +
        renderTrackHead("vst-track-icon-refs", "⧉", "References", "") +
        `<div class="vst-track-cell">${lanes}</div>` +
        `</div>`
    );
};
