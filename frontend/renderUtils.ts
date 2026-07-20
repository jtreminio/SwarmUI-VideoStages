export const escapeAttr = (value: unknown): string =>
    String(value ?? "")
        .replace(/&/g, "&amp;")
        .replace(/"/g, "&quot;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");

const FRAME_ALIGNMENT = 8;

export const framesForClip = (durationSeconds: number, fps: number): number =>
    Math.max(
        1,
        Math.ceil(
            Math.max(0, Math.ceil(durationSeconds * Math.max(1, fps))) /
                FRAME_ALIGNMENT,
        ) *
            FRAME_ALIGNMENT +
            1,
    );

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
