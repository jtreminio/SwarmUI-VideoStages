import { safeFps } from "./timelineDetail";
import type { Clip, VideoStagesConfig } from "./types";

/** Canonical document FPS for timeline math, including the shared fallback. */
export const documentFps = (document: Pick<VideoStagesConfig, "fps">): number =>
    safeFps(document.fps);

export interface ClipTimelineWindow {
    startSeconds: number;
    endSeconds: number;
}

export const clipTimelineWindow = (
    clips: Clip[],
    clipIdx: number,
): ClipTimelineWindow | null => {
    if (clipIdx < 0 || clipIdx >= clips.length) {
        return null;
    }
    const startSeconds = clips
        .slice(0, clipIdx)
        .reduce((sum, clip) => sum + Math.max(0, clip.duration || 0), 0);
    return {
        startSeconds,
        endSeconds: startSeconds + Math.max(0, clips[clipIdx].duration || 0),
    };
};

/** Global audio lanes whose half-open windows intersect the selected clip. */
export const audioTrackIndicesForClipWindow = (
    state: Pick<VideoStagesConfig, "clips" | "audioTracks">,
    clipIdx: number,
): number[] => {
    const clipWindow = clipTimelineWindow(state.clips, clipIdx);
    if (!clipWindow || clipWindow.endSeconds <= clipWindow.startSeconds) {
        return [];
    }
    return (state.audioTracks ?? []).flatMap((track, trackIdx) => {
        const intersects = track.spans.some((span) => {
            const start = span.timelineStartSeconds;
            const length = span.timelineLengthSeconds;
            if (
                typeof start !== "number" ||
                !Number.isFinite(start) ||
                typeof length !== "number" ||
                !Number.isFinite(length) ||
                length <= 0
            ) {
                return false;
            }
            const end = start + length;
            return (
                start < clipWindow.endSeconds && end > clipWindow.startSeconds
            );
        });
        return intersects ? [trackIdx] : [];
    });
};
