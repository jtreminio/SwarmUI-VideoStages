import { architectureDimensionMultiple } from "./architectures/behaviorRegistry";
import { resolvedClipArchitectureId } from "./architectures/clipIdentity";
import type { ArchitectureModelCatalog } from "./architectures/types";
import { ROOT_DIMENSION_STEP } from "./constants";
import { snapDimensions } from "./dimensionSnap";
import type { AuthoringDocument, Clip } from "./types";

export interface DocumentDimensionSnap {
    changed: boolean;
    before: { width: number; height: number };
    after: { width: number; height: number };
    multiple: number;
}

const greatestCommonDivisor = (left: number, right: number): number => {
    let a = Math.abs(Math.round(left));
    let b = Math.abs(Math.round(right));
    while (b !== 0) {
        [a, b] = [b, a % b];
    }
    return a || 1;
};

const leastCommonMultiple = (left: number, right: number): number =>
    Math.abs(left * right) / greatestCommonDivisor(left, right);

/**
 * Every document uses the global /32 grid. Architectures may add stricter
 * per-clip requirements without leaking their policy into this layer.
 */
export const activeDocumentDimensionMultiple = (
    clips: readonly Clip[],
    catalog: ArchitectureModelCatalog | null,
): number =>
    clips.reduce(
        (multiple, clip) =>
            leastCommonMultiple(
                multiple,
                architectureDimensionMultiple(
                    clip,
                    resolvedClipArchitectureId(clip, catalog) ?? "",
                ),
            ),
        ROOT_DIMENSION_STEP,
    );

/**
 * Applies the document-wide grid on a mutation boundary. Inherited
 * dimensions remain live host values and are only previewed/snapped at runtime.
 */
export const snapExplicitDocumentDimensions = (
    state: AuthoringDocument,
    catalog: ArchitectureModelCatalog | null,
): DocumentDimensionSnap => {
    const before = {
        width: Math.round(state.width),
        height: Math.round(state.height),
    };
    const multiple = activeDocumentDimensionMultiple(state.clips, catalog);
    const after = state.dimsExplicit
        ? snapDimensions(before.width, before.height, multiple)
        : before;
    const changed =
        after.width !== before.width || after.height !== before.height;
    if (changed) {
        state.width = after.width;
        state.height = after.height;
    }
    return { changed, before, after, multiple };
};
