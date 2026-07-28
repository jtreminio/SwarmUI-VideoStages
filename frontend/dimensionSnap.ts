import { ROOT_DIMENSION_MAX, ROOT_DIMENSION_STEP } from "./constants";

export interface SnappedDimensions {
    width: number;
    height: number;
}

const AREA_DRIFT_WEIGHT = 0.15;
const SCORE_EPSILON = 1e-12;

const clampToSnapRange = (value: number, multiple: number): number =>
    Math.min(ROOT_DIMENSION_MAX, Math.max(multiple, value));

/**
 * Finds the nearby grid-aligned dimensions that best preserve the requested
 * aspect ratio, with a smaller secondary penalty for changing image area.
 * Mirrored by src/DimensionSnap.cs and pinned by the shared fixture.
 */
export const snapDimensions = (
    width: number,
    height: number,
    multiple = ROOT_DIMENSION_STEP,
): SnappedDimensions => {
    const requestedWidth = Math.max(1, Number.isFinite(width) ? width : 1);
    const requestedHeight = Math.max(1, Number.isFinite(height) ? height : 1);
    const grid = Math.min(
        ROOT_DIMENSION_MAX,
        Math.max(
            ROOT_DIMENSION_STEP,
            Math.round(Number.isFinite(multiple) ? multiple : 0),
        ),
    );
    const floorWidth = clampToSnapRange(
        Math.floor(requestedWidth / grid) * grid,
        grid,
    );
    const ceilWidth = clampToSnapRange(
        Math.ceil(requestedWidth / grid) * grid,
        grid,
    );
    const floorHeight = clampToSnapRange(
        Math.floor(requestedHeight / grid) * grid,
        grid,
    );
    const ceilHeight = clampToSnapRange(
        Math.ceil(requestedHeight / grid) * grid,
        grid,
    );
    const candidates: SnappedDimensions[] = [
        { width: floorWidth, height: floorHeight },
        { width: ceilWidth, height: floorHeight },
        { width: floorWidth, height: ceilHeight },
        { width: ceilWidth, height: ceilHeight },
    ];
    const targetAspect = requestedWidth / requestedHeight;
    const targetArea = requestedWidth * requestedHeight;
    const score = (candidate: SnappedDimensions): number =>
        Math.abs(
            Math.log(candidate.width / candidate.height) -
                Math.log(targetAspect),
        ) +
        AREA_DRIFT_WEIGHT *
            Math.abs(
                Math.log((candidate.width * candidate.height) / targetArea),
            );

    let best = candidates[0];
    let bestScore = score(best);
    for (const candidate of candidates.slice(1)) {
        const candidateScore = score(candidate);
        const candidateArea = candidate.width * candidate.height;
        const bestArea = best.width * best.height;
        if (
            candidateScore < bestScore - SCORE_EPSILON ||
            (Math.abs(candidateScore - bestScore) <= SCORE_EPSILON &&
                candidateArea > bestArea)
        ) {
            best = candidate;
            bestScore = candidateScore;
        }
    }
    return best;
};
