import type {
    ArchitectureCapabilities,
    ArchitectureCatalogEntryDto,
    ArchitectureModelCatalog,
    VideoArchitectureCatalogDto,
    VideoArchitectureId,
} from "../architectures/types";
import type { RootDefaults } from "../types";

export const testArchitectureCapabilities = (
    overrides: Partial<ArchitectureCapabilities> = {},
): ArchitectureCapabilities => ({
    architecture: [
        "generated-entry",
        "sourced-entry",
        "multi-stage",
        "decoded-output",
    ],
    clip: [
        "source-video",
        "prompts",
        "prompt-relay",
        "references",
        "reference-framing",
        "retake",
        "audio-sources",
        "audio-segments",
        "audio-reuse",
        "audio-derived-duration",
        "control-signal-derived-duration",
    ],
    stage: [
        "image-input",
        "video-input",
        "lora",
        "ic-lora",
        "hdr",
        "frame-references",
        "pixel-upscale",
    ],
    output: ["video", "attached-audio"],
    upscaleModes: ["pixel"],
    entryModes: [
        "text-to-video",
        "image-to-video",
        "source-video",
        "refine-video",
    ],
    audioSourceKinds: ["Native", "Upload"],
    ...overrides,
});

export const testArchitectureCatalog = (
    overrides: Partial<ArchitectureModelCatalog> = {},
): ArchitectureModelCatalog => ({
    source: "backend",
    architectures: [
        {
            id: "ltx2",
            label: "LTX Video 2.3",
            defaultProfileId: "ltx-2.3",
            capabilities: testArchitectureCapabilities(),
            profiles: [
                {
                    id: "ltx-2.3",
                    label: "LTX Video 2.3",
                    entryModes: [
                        "text-to-video",
                        "image-to-video",
                        "source-video",
                        "refine-video",
                    ],
                    capabilities: [
                        "sampler-selection",
                        "scheduler-selection",
                        "dimension-rules",
                        "frame-rules",
                        "normal-lora",
                    ],
                    rules: [],
                },
            ],
            boundaryRules: {
                cut: {
                    support: "supported",
                    code: "ltx2.boundary.cut",
                    reason: "Cut is supported.",
                    scope: "boundary",
                    entityId: null,
                    constraints: null,
                },
                continue: {
                    support: "conditional",
                    code: "ltx2.boundary.continue",
                    reason: "Continue requires matching architectures.",
                    scope: "boundary",
                    entityId: null,
                    constraints: {
                        sameArchitecture: true,
                        targetRequiresGeneratedEntry: true,
                        targetRequiresStage: true,
                        targetDisallowsInitialReference: true,
                        frameStep: 8,
                        minFrames: 8,
                        maxFrames: 48,
                        defaultFrames: 8,
                        continuityExtraFrames: 1,
                    },
                },
                crossfade: {
                    support: "conditional",
                    code: "ltx2.boundary.crossfade",
                    reason: "Crossfade requires matching architectures.",
                    scope: "boundary",
                    entityId: null,
                    constraints: {
                        sameArchitecture: true,
                        targetRequiresGeneratedEntry: false,
                        targetRequiresStage: false,
                        targetDisallowsInitialReference: false,
                        frameStep: 8,
                        minFrames: 8,
                        maxFrames: 48,
                        defaultFrames: 8,
                        continuityExtraFrames: 0,
                    },
                },
            },
            rules: [
                {
                    support: "conditional",
                    code: "audio.reuse.requires_three_stages",
                    reason: "Audio reuse needs at least three active stages: generate, capture, then reuse.",
                    scope: "clip",
                    entityId: null,
                    constraints: {
                        minimumActiveStages: 3,
                        failureSeverity: "warning",
                        failureEffect: "disable-feature",
                    },
                },
                {
                    support: "conditional",
                    code: "prompt-relay-dynamic-length-unsupported",
                    reason: "Prompt relay requires a fixed frame count.",
                    scope: "clip",
                    entityId: null,
                    constraints: { requiresFixedFrameCount: true },
                },
                {
                    support: "conditional",
                    code: "retake-frame-references-unsupported",
                    reason: "Retake and frame references are mutually exclusive.",
                    scope: "stage",
                    entityId: null,
                    constraints: {
                        mutuallyExclusive: ["retake", "frameReferences"],
                    },
                },
                {
                    support: "conditional",
                    code: "retake-source-required",
                    reason: "Retake requires source footage.",
                    scope: "clip",
                    entityId: null,
                    constraints: {
                        requiresAnyEntryMode: ["source-video", "refine-video"],
                    },
                },
                {
                    support: "conditional",
                    code: "mixed-hdr-timeline-unsupported",
                    reason: "HDR must be uniform across the timeline.",
                    scope: "architecture",
                    entityId: null,
                    constraints: {
                        uniformTimelineFeature: "hdr",
                        minimumTimelineClips: 2,
                    },
                },
            ],
        },
    ],
    entries: [
        {
            value: "ltx-2.3.safetensors",
            label: "LTX 2.3",
            architectureId: "ltx2",
            modelProfileId: "ltx-2.3",
            modelClassId: "ltx-video",
            compatibilityClassId: "ltx-video",
            frameGrid: 8,
            entryModes: [
                "text-to-video",
                "image-to-video",
                "source-video",
                "refine-video",
            ],
        },
        {
            value: "ltx",
            label: "Synthetic LTX 2.3 alias",
            architectureId: "ltx2",
            modelProfileId: "ltx-2.3",
            modelClassId: "ltx-video",
            compatibilityClassId: "ltx-video",
            frameGrid: 8,
            entryModes: [
                "text-to-video",
                "image-to-video",
                "source-video",
                "refine-video",
            ],
        },
    ],
    ...overrides,
});

export const testArchitectureCatalogDto = (
    catalog: ArchitectureModelCatalog = testArchitectureCatalog(),
): VideoArchitectureCatalogDto => ({
    architectures: structuredClone(catalog.architectures),
    models: catalog.entries.flatMap((entry) =>
        entry.architectureId && entry.modelProfileId
            ? [
                  {
                      modelName: entry.value,
                      architectureId: entry.architectureId,
                      modelProfileId: entry.modelProfileId,
                      modelClassId: entry.modelClassId ?? "test-model-class",
                      compatibilityClassId:
                          entry.compatibilityClassId ??
                          "test-compatibility-class",
                      frameGrid: entry.frameGrid ?? 1,
                      ...(entry.entryAbilities
                          ? { entryAbilities: [...entry.entryAbilities] }
                          : {}),
                      ...(entry.enhancements
                          ? {
                                enhancements: structuredClone(
                                    entry.enhancements,
                                ),
                            }
                          : {}),
                      entryModes: [...entry.entryModes],
                  },
              ]
            : [],
    ),
});

export const testSourceOnlyArchitecture = (): ArchitectureCatalogEntryDto => ({
    id: "none",
    label: "Decoded source only",
    defaultProfileId: "none",
    capabilities: testArchitectureCapabilities({
        architecture: ["sourced-entry", "decoded-output"],
        clip: ["source-video", "audio-sources", "audio-segments"],
        stage: [],
        output: ["video", "attached-audio"],
        upscaleModes: [],
        entryModes: ["source-video"],
        audioSourceKinds: ["Disabled", "Upload"],
    }),
    profiles: [
        {
            id: "none",
            label: "Decoded source only",
            entryModes: ["source-video"],
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
            reason: "Sourced-only clips do not support continuation.",
            scope: "boundary",
            entityId: null,
            constraints: null,
        },
        crossfade: {
            support: "unsupported",
            code: "none.boundary.crossfade.unsupported",
            reason: "Sourced-only clips do not support crossfade.",
            scope: "boundary",
            entityId: null,
            constraints: null,
        },
    },
    rules: [],
});

export const fakeArchitectureCatalog = (
    architectureId: VideoArchitectureId = "test-video",
): ArchitectureModelCatalog => ({
    source: "backend",
    architectures: [
        {
            id: architectureId,
            label: "Test Video",
            defaultProfileId: "test-profile",
            capabilities: testArchitectureCapabilities({
                clip: ["prompts"],
                stage: [],
                upscaleModes: [],
                entryModes: ["text-to-video", "image-to-video", "source-video"],
                audioSourceKinds: ["Native"],
            }),
            profiles: [
                {
                    id: "test-profile",
                    label: "Test Profile",
                    entryModes: [
                        "text-to-video",
                        "image-to-video",
                        "source-video",
                    ],
                    capabilities: [],
                    rules: [],
                },
            ],
            boundaryRules: {
                cut: {
                    support: "supported",
                    code: "test.boundary.cut",
                    reason: "Only cuts are supported.",
                    scope: "boundary",
                    entityId: null,
                    constraints: null,
                },
                continue: {
                    support: "unsupported",
                    code: "test.boundary.continue.unsupported",
                    reason: "Continue is unsupported.",
                    scope: "boundary",
                    entityId: null,
                    constraints: null,
                },
                crossfade: {
                    support: "unsupported",
                    code: "test.boundary.crossfade.unsupported",
                    reason: "Crossfade is unsupported.",
                    scope: "boundary",
                    entityId: null,
                    constraints: null,
                },
            },
            rules: [],
        },
    ],
    entries: [
        {
            value: "test-video.safetensors",
            label: "Test Video",
            architectureId,
            modelProfileId: "test-profile",
            modelClassId: "test-video",
            compatibilityClassId: "test-video",
            frameGrid: 1,
            entryModes: [
                "text-to-video",
                "image-to-video",
                "source-video",
                "refine-video",
            ],
        },
    ],
});

export const testRootDefaults = (
    modelCatalog: ArchitectureModelCatalog = testArchitectureCatalog(),
): RootDefaults => ({
    modelCatalog,
    modelValues: modelCatalog.entries.map((entry) => entry.value),
    modelLabels: modelCatalog.entries.map((entry) => entry.label),
    loraValues: [],
    loraLabels: [],
    loraDefaultWeights: [],
    samplerValues: ["euler"],
    samplerLabels: ["Euler"],
    schedulerValues: ["normal"],
    schedulerLabels: ["Normal"],
    upscaleMethodValues: ["pixel-lanczos"],
    upscaleMethodLabels: ["Lanczos"],
    width: 1024,
    height: 576,
    fps: 24,
    frames: 48,
    control: 0.5,
    controlMin: 0,
    controlMax: 1,
    controlStep: 0.05,
    upscale: 1,
    upscaleMin: 0.25,
    upscaleMax: 4,
    upscaleStep: 0.25,
    steps: 8,
    stepsMin: 1,
    stepsMax: 50,
    stepsStep: 1,
    cfgScale: 1,
    cfgScaleMin: 0,
    cfgScaleMax: 10,
    cfgScaleStep: 0.5,
});
