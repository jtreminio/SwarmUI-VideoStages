export const escapeHtml = (value: unknown): string =>
    String(value ?? "")
        .replace(/&/g, "&amp;")
        .replace(/"/g, "&quot;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");

/**
 * A generated frame count is `k * frameGrid + frameGridOrigin`. Mirrors the backend's
 * `VideoArchitectureDescriptor.FrameGrid` / `FrameGridOrigin` pair.
 */
export interface FrameGridSpec {
    frameGrid: number;
    frameGridOrigin: number;
}

export const NEUTRAL_FRAME_GRID: FrameGridSpec = {
    frameGrid: 1,
    frameGridOrigin: 1,
};

export const framesForClip = (
    durationSeconds: number,
    fps: number,
    spec: FrameGridSpec,
): number => {
    const rawFrameGrid = spec.frameGrid;
    const frameGrid =
        Number.isInteger(rawFrameGrid) && rawFrameGrid > 0 ? rawFrameGrid : 1;
    const rawOrigin = spec.frameGridOrigin;
    const gridOrigin =
        Number.isInteger(rawOrigin) && rawOrigin >= 1 && rawOrigin <= frameGrid
            ? rawOrigin
            : 1;
    const intervals = Math.max(
        0,
        Math.ceil(durationSeconds * Math.max(1, fps)),
    );
    const beyondOrigin = Math.max(0, intervals + 1 - gridOrigin);
    return gridOrigin + Math.ceil(beyondOrigin / frameGrid) * frameGrid;
};

export const snapDurationToFps = (seconds: number, fps: number): number => {
    if (
        !Number.isFinite(seconds) ||
        seconds <= 0 ||
        !Number.isFinite(fps) ||
        fps <= 0
    ) {
        return seconds;
    }

    const frames = Math.max(1, Math.ceil(seconds * fps));
    const aligned = frames / fps;
    return Math.max(0.1, Math.floor(aligned * 10) / 10);
};
