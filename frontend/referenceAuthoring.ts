import type { FrameReferencePosition } from "./architectures/types";
import { clamp, REF_FRAME_MIN } from "./constants";
import type { FrameRefImage } from "./types";

const REFERENCE_FRAME_STEP_FRACTION = 0.1;

const absoluteReferenceFrame = (
    ref: Pick<FrameRefImage, "frame" | "fromEnd">,
    frameMax: number,
): number => {
    const authoredFrame = clamp(Math.round(ref.frame), REF_FRAME_MIN, frameMax);
    return ref.fromEnd
        ? frameMax - authoredFrame + REF_FRAME_MIN
        : authoredFrame;
};

/**
 * Picks a unique attachment frame for a newly added reference.
 *
 * Preferred slots begin at frame 1 and advance in rounded 10%-of-clip
 * increments through the last frame. Once those slots are occupied, allocation
 * wraps to the earliest unused frame. Null means every frame is occupied.
 */
export const nextAvailableReferenceFrame = (
    frameRefs: readonly Pick<FrameRefImage, "frame" | "fromEnd">[],
    rawFrameMax: number,
): number | null => {
    const frameMax =
        Number.isFinite(rawFrameMax) && rawFrameMax >= REF_FRAME_MIN
            ? Math.floor(rawFrameMax)
            : REF_FRAME_MIN;
    const occupied = new Set(
        frameRefs.map((ref) => absoluteReferenceFrame(ref, frameMax)),
    );
    const step = Math.max(
        1,
        Math.round(frameMax * REFERENCE_FRAME_STEP_FRACTION),
    );
    const preferred = new Set<number>();
    for (let index = 0; ; index++) {
        const candidate = Math.min(frameMax, REF_FRAME_MIN + index * step);
        preferred.add(candidate);
        if (candidate >= frameMax) {
            break;
        }
    }
    for (const candidate of preferred) {
        if (!occupied.has(candidate)) {
            return candidate;
        }
    }
    for (let candidate = REF_FRAME_MIN; candidate <= frameMax; candidate++) {
        if (!occupied.has(candidate)) {
            return candidate;
        }
    }
    return null;
};

export const nextAllowedReferencePosition = (
    frameRefs: readonly Pick<FrameRefImage, "frame" | "fromEnd">[],
    rawFrameMax: number,
    allowed: readonly FrameReferencePosition[],
): { frame: number; fromEnd: boolean } | null => {
    if (allowed.includes("any")) {
        const frame = nextAvailableReferenceFrame(frameRefs, rawFrameMax);
        return frame === null ? null : { frame, fromEnd: false };
    }
    if (
        allowed.includes("first") &&
        !frameRefs.some((ref) => ref.frame === REF_FRAME_MIN && !ref.fromEnd)
    ) {
        return { frame: REF_FRAME_MIN, fromEnd: false };
    }
    if (
        allowed.includes("last") &&
        !frameRefs.some((ref) => ref.frame === REF_FRAME_MIN && ref.fromEnd)
    ) {
        return { frame: REF_FRAME_MIN, fromEnd: true };
    }
    return null;
};
