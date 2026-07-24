import { VIDEO_ARCHITECTURE_MODULES } from "./modules";
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

export const videoArchitectureRegistry = createArchitectureRegistry(
    VIDEO_ARCHITECTURE_MODULES.map((module) => module.definition),
);
