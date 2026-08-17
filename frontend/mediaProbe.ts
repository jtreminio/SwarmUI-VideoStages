import { MEDIA_SOURCE_UPLOAD } from "./generatedMediaSource";
import { getVideoStagesHostBridge } from "./host";
import type { InitVideo } from "./types";
import { roundToTenth } from "./utils";

export interface InitVideoProbe {
    durationSeconds: number;
    fps: number | null;
    width: number | null;
    height: number | null;
}

export const initVideoFromProbe = (
    probe: InitVideoProbe | null,
    data: string,
    fileName: string | null,
    clipDuration: number,
): InitVideo => {
    const durationSeconds = roundToTenth(probe?.durationSeconds ?? 0);
    return {
        source: MEDIA_SOURCE_UPLOAD,
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

/** Tears down the probe after every exit. */
const withProbedMedia = <T>(
    src: string,
    timeoutMs: number,
    empty: T,
    onMetadata: (
        video: VideoWithFrameCallback,
        durationSeconds: number,
        finish: (result: T) => void,
    ) => void,
): Promise<T> =>
    new Promise((resolve) => {
        const video =
            getVideoStagesHostBridge().createInitVideoElement() as VideoWithFrameCallback;
        video.muted = true;
        video.preload = "auto";
        let settled = false;
        const finish = (result: T): void => {
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
        const timer = setTimeout(() => finish(empty), timeoutMs);
        video.addEventListener("error", () => finish(empty));
        video.addEventListener("loadedmetadata", () => {
            const durationSeconds = Number.isFinite(video.duration)
                ? video.duration
                : 0;
            if (!(durationSeconds > 0)) {
                finish(empty);
                return;
            }
            onMetadata(video, durationSeconds, finish);
        });
        video.src = src;
    });

export const probeInitVideo = (
    src: string,
    timeoutMs = 8000,
): Promise<InitVideoProbe | null> =>
    withProbedMedia<InitVideoProbe | null>(
        src,
        timeoutMs,
        null,
        (video, durationSeconds, finish) => {
            const dimensions = {
                width: video.videoWidth > 0 ? video.videoWidth : null,
                height: video.videoHeight > 0 ? video.videoHeight : null,
            };
            const requestFrame = video.requestVideoFrameCallback?.bind(video);
            if (!requestFrame) {
                finish({ durationSeconds, fps: null, ...dimensions });
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
                        ...dimensions,
                    });
                    return;
                }
                requestFrame(step);
            };
            requestFrame(step);
            video
                .play()
                ?.catch(() =>
                    finish({ durationSeconds, fps: null, ...dimensions }),
                );
        },
    );

/** Reads duration without playback. Returns 0 when unavailable. */
export const probeMediaDurationSeconds = (
    src: string,
    timeoutMs = 8000,
): Promise<number> =>
    withProbedMedia<number>(src, timeoutMs, 0, (_video, duration, finish) =>
        finish(duration),
    );
