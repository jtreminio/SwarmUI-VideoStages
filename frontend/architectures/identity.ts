import { architectureForModel } from "./catalog";
import type { ArchitectureModelCatalog, VideoArchitectureId } from "./types";

export const normalizeClipArchitecture = (
    rawArchitecture: unknown,
    stageZeroModel: string | null,
    catalog?: ArchitectureModelCatalog,
): VideoArchitectureId => {
    const persisted = `${rawArchitecture ?? ""}`.trim();
    if (persisted) {
        return persisted;
    }
    const fromCatalog =
        catalog && stageZeroModel
            ? architectureForModel(catalog, stageZeroModel)
            : null;
    if (fromCatalog) {
        return fromCatalog;
    }
    return "unsupported";
};
