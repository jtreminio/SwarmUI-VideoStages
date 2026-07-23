import type { VideoArchitectureDefinition } from "../types";

export const NONE_ARCHITECTURE_ID = "none";

export const noneArchitecture: VideoArchitectureDefinition = {
    id: NONE_ARCHITECTURE_ID,
    label: "Decoded source only",
    defaultProfileId: NONE_ARCHITECTURE_ID,
    capabilities: {
        architecture: ["sourced-entry", "decoded-output"],
        clip: ["source-video", "audio-sources", "audio-segments"],
        stage: [],
        output: ["video", "attached-audio"],
        upscaleModes: [],
        entryModes: ["source-video"],
        audioSourceKinds: ["Disabled", "Upload"],
    },
    profiles: [
        {
            id: NONE_ARCHITECTURE_ID,
            label: "Decoded source only",
            capabilities: [],
            rules: [],
        },
    ],
    boundaryRules: {
        cut: {
            support: "supported",
            code: "none.boundary.cut",
            reason: "Decoded sourced clips can be joined with a hard cut.",
            scope: "boundary",
            entityId: null,
            constraints: null,
        },
        continue: {
            support: "unsupported",
            code: "none.boundary.continue.unsupported",
            reason: "A sourced-only clip has no architecture stage that can consume continuity.",
            scope: "boundary",
            entityId: null,
            constraints: null,
        },
        crossfade: {
            support: "unsupported",
            code: "none.boundary.crossfade.unsupported",
            reason: "Architecture-neutral sourced clips currently support cut joins only.",
            scope: "boundary",
            entityId: null,
            constraints: null,
        },
    },
    rules: [],
    resolveModelProfile: () => null,
};
