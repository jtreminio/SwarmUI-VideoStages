import { clamp } from "./constants";
import { snapDurationToFps } from "./renderUtils";
import { roundToTenth } from "./utils";

export interface SourceRange {
    startSeconds: number;
    lengthSeconds: number;
}

export interface TrimRange {
    inSeconds: number;
    outSeconds: number;
}

export interface TrimLimits {
    limitSeconds: number;
    minLengthSeconds: number;
    /** 0 leaves lengths unsnapped. */
    fps: number;
}

export const clampStartLength = (
    start: number,
    length: number,
    duration: number,
    minLength: number,
): { start: number; length: number } => {
    const clampedStart = clamp(start, 0, Math.max(0, duration - minLength));
    const clampedLength = clamp(
        length,
        minLength,
        Math.max(minLength, duration - clampedStart),
    );
    return { start: clampedStart, length: clampedLength };
};

/**
 * How far the out point may reach. A probe that could not measure the file
 * leaves the current range as the only thing known about it, so the range
 * becomes its own ceiling — trimming inward still works, widening does not.
 */
export const sourceLimitSeconds = (
    source: SourceRange & { durationSeconds: number },
): number =>
    source.durationSeconds > 0
        ? source.durationSeconds
        : source.startSeconds + source.lengthSeconds;

/** Rounds the derived Out point to remove floating-point drift. */
export const toInOut = ({
    startSeconds,
    lengthSeconds,
}: SourceRange): TrimRange => ({
    inSeconds: startSeconds,
    outSeconds: roundToTenth(startSeconds + lengthSeconds),
});

/** Rounds, fps-snaps, then clamps the range. */
export const fromInOut = (
    { inSeconds, outSeconds }: TrimRange,
    { limitSeconds, minLengthSeconds, fps }: TrimLimits,
): SourceRange => {
    const start = roundToTenth(inSeconds);
    const length = snapDurationToFps(roundToTenth(outSeconds) - start, fps);
    const clamped = clampStartLength(
        start,
        length,
        limitSeconds,
        minLengthSeconds,
    );
    return { startSeconds: clamped.start, lengthSeconds: clamped.length };
};

export const setInPoint = (
    current: SourceRange,
    inSeconds: number,
    limits: TrimLimits,
): SourceRange => {
    const { outSeconds } = toInOut(current);
    return fromInOut(
        {
            inSeconds: Math.max(
                0,
                Math.min(inSeconds, outSeconds - limits.minLengthSeconds),
            ),
            outSeconds,
        },
        limits,
    );
};

export const setOutPoint = (
    current: SourceRange,
    outSeconds: number,
    limits: TrimLimits,
): SourceRange => {
    const { inSeconds } = toInOut(current);
    return fromInOut(
        {
            inSeconds,
            outSeconds: Math.min(
                limits.limitSeconds,
                Math.max(outSeconds, inSeconds + limits.minLengthSeconds),
            ),
        },
        limits,
    );
};

export const slideRange = (
    current: SourceRange,
    inSeconds: number,
    limits: TrimLimits,
): SourceRange => {
    const length = current.lengthSeconds;
    const start = Math.max(
        0,
        Math.min(
            roundToTenth(inSeconds),
            Math.max(0, limits.limitSeconds - length),
        ),
    );
    return fromInOut({ inSeconds: start, outSeconds: start + length }, limits);
};

/** Past Out, Mark In selects a new window instead of collapsing the old one. */
export const markInPoint = (
    current: SourceRange,
    inSeconds: number,
    limits: TrimLimits,
): SourceRange =>
    inSeconds > toInOut(current).outSeconds
        ? slideRange(current, inSeconds, limits)
        : setInPoint(current, inSeconds, limits);
