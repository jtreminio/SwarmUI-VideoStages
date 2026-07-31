import { createCapabilityViewResolver } from "../architectures/policy";
import type {
    ArchitectureCapabilities,
    ArchitectureCatalogEntryDto,
    ArchitectureModelCatalog,
    VideoArchitectureCatalogDto,
    VideoArchitectureId,
} from "../architectures/types";
import type { AuthoringTransactionSnapshot } from "../authoringSnapshot";
import type { RootDefaults } from "../types";

export const testArchitectureCapabilities = (
    overrides: Partial<ArchitectureCapabilities> = {},
): ArchitectureCapabilities => ({
    architecture: ["generated-entry", "init-video-entry"],
    clip: [
        "init-video",
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
        "frame-references",
        "pixel-upscale",
    ],
    upscaleModes: ["pixel"],
    entryModes: ["text-to-video", "image-to-video", "init-video"],
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
            capabilities: testArchitectureCapabilities(),
            boundaryRules: {
                cut: {
                    support: "supported",
                    code: "ltx2.boundary.cut",
                    reason: "Cut is supported.",
                    scope: "boundary",
                    constraints: null,
                },
                continue: {
                    support: "conditional",
                    code: "ltx2.boundary.continue",
                    reason: "Continue requires matching architectures.",
                    scope: "boundary",
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
                    constraints: {
                        minimumActiveStages: 3,
                    },
                },
                {
                    support: "conditional",
                    code: "prompt-relay-dynamic-length-unsupported",
                    reason: "Prompt relay requires a fixed frame count.",
                    scope: "clip",
                    constraints: null,
                },
                {
                    support: "conditional",
                    code: "retake-frame-references-unsupported",
                    reason: "Retake and frame references are mutually exclusive.",
                    scope: "stage",
                    constraints: {
                        mutuallyExclusive: ["retake", "frameReferences"],
                    },
                },
                {
                    support: "conditional",
                    code: "retake-source-required",
                    reason: "Retake requires source footage.",
                    scope: "clip",
                    constraints: {
                        requiresAnyEntryMode: ["init-video"],
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
            enhancements: { referencePositions: ["any"] },
            entryModes: ["text-to-video", "image-to-video", "init-video"],
        },
        {
            value: "ltx",
            label: "Synthetic LTX 2.3 alias",
            architectureId: "ltx2",
            modelProfileId: "ltx-2.3",
            modelClassId: "ltx-video",
            compatibilityClassId: "ltx-video",
            frameGrid: 8,
            enhancements: { referencePositions: ["any"] },
            entryModes: ["text-to-video", "image-to-video", "init-video"],
        },
    ],
    ...overrides,
});

export const testArchitectureCatalogDto = (
    catalog: ArchitectureModelCatalog = testArchitectureCatalog(),
): VideoArchitectureCatalogDto => ({
    schemaVersion: 2,
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
                      capabilities: structuredClone(
                          entry.capabilities ??
                              catalog.architectures.find(
                                  (architecture) =>
                                      architecture.id === entry.architectureId,
                              )?.capabilities ??
                              testArchitectureCapabilities(),
                      ),
                      ...(entry.entryAbilities
                          ? { entryAbilities: [...entry.entryAbilities] }
                          : {
                                entryAbilities: [
                                    ...(entry.entryModes.includes(
                                        "text-to-video",
                                    )
                                        ? ["text"]
                                        : []),
                                    ...(entry.entryModes.some((mode) =>
                                        [
                                            "image-to-video",
                                            "init-video",
                                        ].includes(mode),
                                    )
                                        ? ["image"]
                                        : []),
                                ],
                            }),
                      enhancements: structuredClone(
                          entry.enhancements ?? { referencePositions: [] },
                      ),
                  },
              ]
            : [],
    ),
});

export const testSourceOnlyArchitecture = (): ArchitectureCatalogEntryDto => ({
    id: "none",
    label: "Decoded source only",
    capabilities: testArchitectureCapabilities({
        architecture: ["init-video-entry"],
        clip: ["init-video", "audio-sources", "audio-segments"],
        stage: [],
        upscaleModes: [],
        entryModes: ["init-video"],
        audioSourceKinds: ["Disabled", "Upload"],
    }),
    boundaryRules: {
        cut: {
            support: "supported",
            code: "none.boundary.cut",
            reason: "Decoded init-video clips can be joined with a hard cut.",
            scope: "boundary",
            constraints: null,
        },
        continue: {
            support: "unsupported",
            code: "none.boundary.continue.unsupported",
            reason: "InitVideo-only clips do not support continuation.",
            scope: "boundary",
            constraints: null,
        },
        crossfade: {
            support: "unsupported",
            code: "none.boundary.crossfade.unsupported",
            reason: "InitVideo-only clips do not support crossfade.",
            scope: "boundary",
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
            capabilities: testArchitectureCapabilities({
                clip: ["prompts"],
                stage: [],
                upscaleModes: [],
                entryModes: ["text-to-video", "image-to-video", "init-video"],
                audioSourceKinds: ["Native"],
            }),
            boundaryRules: {
                cut: {
                    support: "supported",
                    code: "test.boundary.cut",
                    reason: "Only cuts are supported.",
                    scope: "boundary",
                    constraints: null,
                },
                continue: {
                    support: "unsupported",
                    code: "test.boundary.continue.unsupported",
                    reason: "Continue is unsupported.",
                    scope: "boundary",
                    constraints: null,
                },
                crossfade: {
                    support: "unsupported",
                    code: "test.boundary.crossfade.unsupported",
                    reason: "Crossfade is unsupported.",
                    scope: "boundary",
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
            entryModes: ["text-to-video", "image-to-video", "init-video"],
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

export const testAuthoringTransactionSnapshot = (
    modelCatalog: ArchitectureModelCatalog = testArchitectureCatalog(),
    generatedEntryMode: "text-to-video" | "image-to-video" = "text-to-video",
): AuthoringTransactionSnapshot => ({
    catalogStatus: {
        status: "ready",
        catalog: testArchitectureCatalogDto(modelCatalog),
        error: null,
    },
    defaults: testRootDefaults(modelCatalog),
    capabilities: createCapabilityViewResolver(modelCatalog),
    generatedEntryMode,
});
