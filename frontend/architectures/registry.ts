import { ltx2Architecture } from "./ltx2/definition";
import { noneArchitecture } from "./none/definition";
import type {
    ArchitectureRegistry,
    VideoArchitectureDefinition,
    VideoArchitectureId,
} from "./types";

export const createArchitectureRegistry = (
    initial: readonly VideoArchitectureDefinition[] = [],
): ArchitectureRegistry => {
    const byId = new Map<VideoArchitectureId, VideoArchitectureDefinition>();
    for (const definition of initial) {
        if (byId.has(definition.id)) {
            throw new Error(`Duplicate video architecture '${definition.id}'.`);
        }
        byId.set(definition.id, definition);
    }
    return {
        definitions: () => [...byId.values()],
        get: (id) => byId.get(id) ?? null,
        resolveModel: (model) => {
            for (const definition of byId.values()) {
                const profileId = definition.resolveModelProfile(model);
                if (profileId) {
                    return { definition, profileId };
                }
            }
            return null;
        },
    };
};

/**
 * Production intentionally registers only supported architectures. WAN is not
 * registered, so it cannot leak into the catalog or authoring UI.
 */
export const videoArchitectureRegistry = createArchitectureRegistry([
    ltx2Architecture,
    noneArchitecture,
]);
