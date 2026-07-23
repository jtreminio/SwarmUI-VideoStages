import type { Clip } from "../../types";
import { reconcileClipArchitectureIdentity } from "../clipIdentity";
import type { ArchitectureModelCatalog } from "../types";

/**
 * Keeps source-only identity derived from authored stages. Authored stage 0
 * owns the clip identity even when it is skipped.
 */
export const reconcileSourcedClipIdentity = (
    clip: Clip,
    catalog: ArchitectureModelCatalog,
): void => {
    reconcileClipArchitectureIdentity(clip, catalog);
};
