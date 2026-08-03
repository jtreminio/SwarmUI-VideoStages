import { architectureForModel } from "./catalog";
import type { ArchitectureModelCatalog, VideoArchitectureId } from "./types";

/**
 * The persisted hint exists to explain or repair a document whose model no longer resolves, so
 * the live catalog wins whenever it can answer. Preferring the hint would pin a clip to whatever
 * architecture claimed its model when it was last saved.
 */
export const normalizeClipArchitecture = (
    rawArchitecture: unknown,
    stageZeroModel: string | null,
    catalog?: ArchitectureModelCatalog,
): VideoArchitectureId => {
    const fromCatalog =
        catalog && stageZeroModel
            ? architectureForModel(catalog, stageZeroModel)
            : null;
    if (fromCatalog) {
        return fromCatalog;
    }
    return `${rawArchitecture ?? ""}`.trim() || "unsupported";
};
