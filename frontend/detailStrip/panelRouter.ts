import { clamp } from "../constants";
import { getState } from "../persistence";
import { stageChipLabel } from "../timelineDetail";
import type { Clip, TimelineSelection } from "../types";
import { roundToTenth } from "../utils";
import { buildAudioBody } from "./audioPanel";
import { buildTimelineAudioSegmentsBody } from "./audioTracksPanel";
import { buildBoundaryBody } from "./boundaryPanel";
import { buildClipBody } from "./clipPanel";
import type { DetailStripContext } from "./context";
import { buildPromptMajorBody, buildPromptMinorBody } from "./promptPanels";
import { openTimelineAuthoringSettingsModal } from "./settingsModal";
import { buildSettingsBody } from "./settingsPanel";

export const clampDetailSelection = (
    selection: TimelineSelection,
    clips: Clip[],
): TimelineSelection => {
    if (selection.kind === "none") {
        return selection;
    }
    if (selection.kind === "boundary") {
        return selection.leftClipIdx >= 0 &&
            selection.leftClipIdx <= clips.length - 2
            ? selection
            : { kind: "none" };
    }
    if (selection.kind === "audio-track") {
        const tracks = getState().audioTracks ?? [];
        return tracks[selection.trackIdx] ? selection : { kind: "none" };
    }
    if (selection.clipIdx < 0 || selection.clipIdx >= clips.length) {
        return { kind: "none" };
    }
    const clip = clips[selection.clipIdx];
    if (selection.kind === "clip") {
        if (clip.stages.length === 0) {
            return { kind: "clip", clipIdx: selection.clipIdx, stageIdx: 0 };
        }
        const stageIdx = clamp(selection.stageIdx, 0, clip.stages.length - 1);
        return stageIdx === selection.stageIdx
            ? selection
            : { kind: "clip", clipIdx: selection.clipIdx, stageIdx };
    }
    if (selection.kind === "ref") {
        return selection.refIdx >= 0 && selection.refIdx < clip.refs.length
            ? selection
            : { kind: "none" };
    }
    if (selection.kind === "ic-lora") {
        return selection.entryIdx >= 0 &&
            selection.entryIdx < clip.icLoras.length
            ? selection
            : { kind: "clip", clipIdx: selection.clipIdx, stageIdx: 0 };
    }
    if (selection.kind === "prompt-minor") {
        const windows = clip.promptWindows ?? [];
        return selection.windowIdx >= 0 && selection.windowIdx < windows.length
            ? selection
            : { kind: "none" };
    }
    if (selection.kind === "retake") {
        // Retake is a single-instance clip concern. Keep its selection valid
        // while empty so deleting it leaves the same section open with its Add
        // action instead of jumping the sidebar back to the top.
        return selection;
    }
    return selection;
};

export const detailBreadcrumb = (
    selection: TimelineSelection,
    clips: Clip[],
): string => {
    switch (selection.kind) {
        case "clip":
            return clips[selection.clipIdx]?.stages.length === 0
                ? `Clip ${selection.clipIdx} · Source only`
                : `Clip ${selection.clipIdx} · ${stageChipLabel(selection.stageIdx)}`;
        case "ref":
            return `Ref${selection.refIdx} · Clip ${selection.clipIdx}`;
        case "ic-lora":
            return `IC-LoRA ${selection.entryIdx} · Clip ${selection.clipIdx}`;
        case "audio":
            return `Audio · Clip ${selection.clipIdx}`;
        case "audio-track":
            return `Audio segment S${selection.trackIdx}`;
        case "boundary":
            return `Boundary · Clip ${selection.leftClipIdx} → ${selection.leftClipIdx + 1}`;
        case "prompt-major":
            return `Prompts · Clip ${selection.clipIdx}`;
        case "prompt-minor": {
            const window =
                clips[selection.clipIdx]?.promptWindows?.[selection.windowIdx];
            if (!window) {
                return `Relay · Clip ${selection.clipIdx}`;
            }
            const start = roundToTenth(window.start);
            const end = roundToTenth(window.start + window.duration);
            return `Relay ${start}–${end}s · Clip ${selection.clipIdx}`;
        }
        case "retake": {
            const retake = clips[selection.clipIdx]?.retake;
            if (!retake) {
                return `Retake · Clip ${selection.clipIdx}`;
            }
            const start = roundToTenth(retake.startSeconds);
            const end = roundToTenth(
                retake.startSeconds + retake.lengthSeconds,
            );
            return `Retake · Clip ${selection.clipIdx} · ${start}–${end} s`;
        }
        default:
            return "Timeline settings";
    }
};

export const buildDetailHeader = (
    selection: TimelineSelection,
    clips: Clip[],
): HTMLElement => {
    const header = document.createElement("div");
    header.className = "vst-detail-head";
    const breadcrumb = document.createElement("span");
    breadcrumb.className = "vst-detail-crumb";
    breadcrumb.textContent = detailBreadcrumb(selection, clips);

    const settings = document.createElement("button");
    settings.type = "button";
    settings.className = "basic-button small-button vst-detail-settings-button";
    settings.textContent = "⚙";
    settings.title = "Timeline settings";
    settings.setAttribute("aria-label", settings.title);
    settings.addEventListener("click", openTimelineAuthoringSettingsModal);
    header.append(breadcrumb, settings);
    return header;
};

export const buildDetailPanelBody = (
    context: DetailStripContext,
    selection: TimelineSelection,
    clips: Clip[],
): HTMLElement => {
    switch (selection.kind) {
        case "clip":
            return buildClipBody(context, selection, clips);
        case "ref":
            return buildClipBody(context, selection, clips);
        case "ic-lora":
            return buildClipBody(context, selection, clips);
        case "audio":
            return buildAudioBody(context, selection, clips);
        case "audio-track":
            return buildTimelineAudioSegmentsBody(
                context,
                getState(),
                selection,
            );
        case "prompt-major":
            return buildPromptMajorBody(context, selection, clips);
        case "prompt-minor":
            return buildPromptMinorBody(context, selection, clips);
        case "retake":
            return buildClipBody(context, selection, clips);
        case "boundary":
            return buildBoundaryBody(context, selection, clips);
        default:
            return buildSettingsBody(context, { kind: "none" });
    }
};
