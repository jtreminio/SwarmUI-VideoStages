import { clamp } from "../constants";
import { stageChipLabel } from "../timelineDetail";
import type { Clip, TimelineSelection } from "../types";
import { roundToTenth } from "../utils";
import { buildAudioBody } from "./audioPanel";
import { buildBoundaryBody } from "./boundaryPanel";
import { buildClipBody } from "./clipPanel";
import type { DetailStripContext } from "./context";
import { buildPromptMajorBody, buildPromptMinorBody } from "./promptPanels";
import { buildRefBody } from "./refPanel";
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
    if (selection.kind === "prompt-minor") {
        const windows = clip.promptWindows ?? [];
        return selection.windowIdx >= 0 && selection.windowIdx < windows.length
            ? selection
            : { kind: "none" };
    }
    if (selection.kind === "retake") {
        return clip.retake ? selection : { kind: "none" };
    }
    if (selection.kind === "audio-segment") {
        const segments = clip.audioSegments ?? [];
        if (segments.length === 0) {
            return { kind: "audio", clipIdx: selection.clipIdx };
        }
        const segIdx = clamp(selection.segIdx, 0, segments.length - 1);
        return segIdx === selection.segIdx
            ? selection
            : {
                  kind: "audio-segment",
                  clipIdx: selection.clipIdx,
                  segIdx,
              };
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
                ? `Clip ${selection.clipIdx + 1} · Source only`
                : `Clip ${selection.clipIdx + 1} · ${stageChipLabel(selection.stageIdx)}`;
        case "ref":
            return `Ref ${selection.refIdx + 1} · Clip ${selection.clipIdx + 1}`;
        case "audio":
            return `Audio · Clip ${selection.clipIdx + 1}`;
        case "audio-segment": {
            const segment =
                clips[selection.clipIdx]?.audioSegments?.[selection.segIdx];
            if (!segment) {
                return `Audio segment · Clip ${selection.clipIdx + 1}`;
            }
            const start = roundToTenth(segment.startSeconds);
            const end = roundToTenth(
                segment.startSeconds + segment.lengthSeconds,
            );
            return `Audio segment · Clip ${selection.clipIdx + 1} · ${start}–${end} s`;
        }
        case "boundary":
            return `Boundary · Clip ${selection.leftClipIdx + 1} → ${selection.leftClipIdx + 2}`;
        case "prompt-major":
            return `Prompt · Clip ${selection.clipIdx + 1}`;
        case "prompt-minor": {
            const window =
                clips[selection.clipIdx]?.promptWindows?.[selection.windowIdx];
            if (!window) {
                return `Relay · Clip ${selection.clipIdx + 1}`;
            }
            const start = roundToTenth(window.start);
            const end = roundToTenth(window.start + window.duration);
            return `Relay ${start}–${end}s · Clip ${selection.clipIdx + 1}`;
        }
        case "retake": {
            const retake = clips[selection.clipIdx]?.retake;
            if (!retake) {
                return `Retake · Clip ${selection.clipIdx + 1}`;
            }
            const start = roundToTenth(retake.startSeconds);
            const end = roundToTenth(
                retake.startSeconds + retake.lengthSeconds,
            );
            return `Retake · Clip ${selection.clipIdx + 1} · ${start}–${end} s`;
        }
        default:
            return "Timeline settings";
    }
};

export const buildDetailHeader = (
    selection: TimelineSelection,
    clips: Clip[],
    collapsed: boolean,
    actions: {
        clearSelection: () => void;
        toggleCollapsed: () => void;
    },
): HTMLElement => {
    const header = document.createElement("div");
    header.className = "vst-detail-head";
    const breadcrumb = document.createElement("span");
    breadcrumb.className = "vst-detail-crumb";
    breadcrumb.textContent = detailBreadcrumb(selection, clips);

    const clear = document.createElement("button");
    clear.type = "button";
    clear.className = "basic-button small-button vst-detail-clear";
    clear.textContent = "Clear";
    clear.title = "Clear selection (show timeline settings)";
    clear.setAttribute("aria-label", clear.title);
    clear.hidden = selection.kind === "none";
    clear.addEventListener("click", actions.clearSelection);

    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "basic-button small-button vst-detail-collapse";
    toggle.textContent = collapsed ? "▸" : "▾";
    toggle.title = collapsed ? "Expand detail strip" : "Collapse detail strip";
    toggle.setAttribute("aria-label", toggle.title);
    toggle.addEventListener("click", actions.toggleCollapsed);
    header.append(breadcrumb, clear, toggle);
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
            return buildRefBody(context, selection, clips);
        case "audio":
            return buildAudioBody(context, selection, clips);
        case "audio-segment":
            return buildAudioBody(context, selection, clips);
        case "prompt-major":
            return buildPromptMajorBody(context, selection, clips);
        case "prompt-minor":
            return buildPromptMinorBody(context, selection, clips);
        case "retake":
            return buildClipBody(
                context,
                {
                    kind: "clip",
                    clipIdx: selection.clipIdx,
                    stageIdx: 0,
                },
                clips,
            );
        case "boundary":
            return buildBoundaryBody(context, selection, clips);
        default:
            return buildSettingsBody(context);
    }
};
