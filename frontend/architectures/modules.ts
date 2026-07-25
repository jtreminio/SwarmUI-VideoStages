import type { ArchitectureBehavior } from "./behaviorRegistry";
import { ltx2Behavior } from "./ltx2/behavior";
import { ltx2Architecture } from "./ltx2/definition";
import { noneArchitecture } from "./none/definition";
import type { VideoArchitectureDefinition } from "./types";

/**
 * One registration per architecture: its catalog definition plus the pure
 * authoring behavior that owns architecture-specific repairs.
 *
 * The detail strip's DOM panel slot deliberately stays in `authoringPanels.ts`
 * instead of joining this record: panel modules import the persistence layer,
 * which resolves the catalog through this registry, so registering them here
 * would make the module graph circular.
 */
export interface VideoArchitectureModule {
    definition: VideoArchitectureDefinition;
    behavior: ArchitectureBehavior | null;
}

/**
 * Production intentionally registers only supported architectures. WAN is not
 * registered, so it cannot leak into the catalog or authoring UI.
 */
export const VIDEO_ARCHITECTURE_MODULES: readonly VideoArchitectureModule[] = [
    { definition: ltx2Architecture, behavior: ltx2Behavior },
    { definition: noneArchitecture, behavior: null },
];
