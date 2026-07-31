/**
 * Metadata probing for clip source videos. Duration comes from the browser's
 * `loadedmetadata`; fps is estimated by sampling `requestVideoFrameCallback`
 * mediaTime deltas over a short muted playback — the browser exposes no direct
 * fps property. Both are best-effort UI conveniences: the backend re-detects
 * the real fps at generation time, so a failed probe (unsupported codec,
 * jsdom in tests) still yields a usable clip.
 */

import { getVideoStagesHostBridge } from "./host";
import type { InitVideo } from "./types";
import { roundToTenth } from "./utils";

export interface InitVideoProbe {
    durationSeconds: number;
    fps: number | null;
}

/**
 * A probe is best-effort, so both callers fall back to the authored clip length
 * when it reports no duration. Kept here so the two authoring paths — the detail
 * strip picker and the Refine Video button — cannot drift on that fallback.
 */
export const initVideoFromProbe = (
    probe: InitVideoProbe | null,
    data: string,
    fileName: string | null,
    clipDuration: number,
): InitVideo => {
    const durationSeconds = roundToTenth(probe?.durationSeconds ?? 0);
    return {
        data,
        fileName,
        fps: probe?.fps ?? 0,
        durationSeconds,
        startSeconds: 0,
        lengthSeconds: durationSeconds > 0 ? durationSeconds : clipDuration,
    };
};

interface VideoFrameMetadataLike {
    mediaTime: number;
}

type VideoWithFrameCallback = HTMLVideoElement & {
    requestVideoFrameCallback?: (
        callback: (now: number, metadata: VideoFrameMetadataLike) => void,
    ) => number;
};

const FPS_SAMPLE_FRAMES = 12;
const MIN_FRAME_DELTA_SECONDS = 0.0005;
const MAX_PLAUSIBLE_FPS = 240;

/**
 * Median frame-to-frame delta of the sampled presentation times, inverted and
 * rounded to a whole fps. Returns null when there are too few distinct deltas
 * to trust (short files, stalled playback) or the result is implausible.
 */
export const estimateFpsFromMediaTimes = (
    mediaTimes: number[],
): number | null => {
    const deltas: number[] = [];
    for (let i = 1; i < mediaTimes.length; i++) {
        const delta = mediaTimes[i] - mediaTimes[i - 1];
        if (delta > MIN_FRAME_DELTA_SECONDS) {
            deltas.push(delta);
        }
    }
    if (deltas.length < 4) {
        return null;
    }
    deltas.sort((a, b) => a - b);
    const median = deltas[Math.floor(deltas.length / 2)];
    const fps = Math.round(1 / median);
    return fps >= 1 && fps <= MAX_PLAUSIBLE_FPS ? fps : null;
};

export const probeInitVideo = (
    src: string,
    timeoutMs = 8000,
): Promise<InitVideoProbe | null> =>
    new Promise((resolve) => {
        const video =
            getVideoStagesHostBridge().createInitVideoElement() as VideoWithFrameCallback;
        video.muted = true;
        video.preload = "auto";
        let settled = false;
        const finish = (result: InitVideoProbe | null): void => {
            if (settled) {
                return;
            }
            settled = true;
            clearTimeout(timer);
            video.pause();
            video.removeAttribute("src");
            video.load();
            resolve(result);
        };
        const timer = setTimeout(() => finish(null), timeoutMs);
        video.addEventListener("error", () => finish(null));
        video.addEventListener("loadedmetadata", () => {
            const durationSeconds = Number.isFinite(video.duration)
                ? video.duration
                : 0;
            if (!(durationSeconds > 0)) {
                finish(null);
                return;
            }
            const requestFrame = video.requestVideoFrameCallback?.bind(video);
            if (!requestFrame) {
                finish({ durationSeconds, fps: null });
                return;
            }
            const mediaTimes: number[] = [];
            const step = (
                _now: number,
                metadata: VideoFrameMetadataLike,
            ): void => {
                mediaTimes.push(metadata.mediaTime);
                if (
                    mediaTimes.length >= FPS_SAMPLE_FRAMES ||
                    metadata.mediaTime >= durationSeconds
                ) {
                    finish({
                        durationSeconds,
                        fps: estimateFpsFromMediaTimes(mediaTimes),
                    });
                    return;
                }
                requestFrame(step);
            };
            requestFrame(step);
            video.play()?.catch(() => finish({ durationSeconds, fps: null }));
        });
        video.src = src;
    });
