import type { GeneratedEntryMode } from "../architectures/generatedFeatures";
import { createCapabilityViewResolver } from "../architectures/policy";
import type {
    ArchitectureCapabilities,
    ArchitectureCatalogEntryDto,
    ArchitectureModelCatalog,
    ArchitectureModelEntry,
    VideoArchitectureCatalogDto,
    VideoArchitectureId,
} from "../architectures/types";
import type { AuthoringTransactionSnapshot } from "../authoringSnapshot";
import type { RootDefaults } from "../types";

export const testArchitectureCapabilities = (
    overrides: Partial<ArchitectureCapabilities> = {},
): ArchitectureCapabilities => ({
    features: [
        "promptRelay",
        "frameReferences",
        "stageReferenceStrengths",
        "referenceFraming",
        "retake",
        "audioBoundaryCarry",
        "latentUpscale",
        "latentModelUpscale",
        "audioReuse",
        "audioDerivedDuration",
        "icLora",
    ],
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
                    constraints: null,
                },
                continue: {
                    support: "conditional",
                    code: "ltx2.boundary.continue",
                    reason: "Continue requires matching architectures.",
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
                        continueMode: "overlap",
                    },
                },
                crossfade: {
                    support: "conditional",
                    code: "ltx2.boundary.crossfade",
                    reason: "Crossfade requires matching architectures.",
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
                        continueMode: "overlap",
                    },
                },
            },
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
            frameGridOrigin: 1,
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
            frameGridOrigin: 1,
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
                      frameGridOrigin: entry.frameGridOrigin ?? 1,
                      capabilities: structuredClone(
                          entry.capabilities ??
                              catalog.architectures.find(
                                  (architecture) =>
                                      architecture.id === entry.architectureId,
                              )?.capabilities ??
                              testArchitectureCapabilities(),
                      ),
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
        features: [],
        entryModes: ["init-video"],
        audioSourceKinds: ["Disabled", "Upload"],
    }),
    boundaryRules: {
        cut: {
            support: "supported",
            code: "none.boundary.cut",
            reason: "Decoded init-video clips can be joined with a hard cut.",
            constraints: null,
        },
        continue: {
            support: "unsupported",
            code: "none.boundary.continue.unsupported",
            reason: "InitVideo-only clips do not support continuation.",
            constraints: null,
        },
        crossfade: {
            support: "unsupported",
            code: "none.boundary.crossfade.unsupported",
            reason: "InitVideo-only clips do not support crossfade.",
            constraints: null,
        },
    },
});

/** Mirrors `WanArchitectureModule.Descriptor`. */
export const testWanArchitecture = (): ArchitectureCatalogEntryDto => ({
    id: "wan22",
    label: "WAN Video",
    capabilities: testArchitectureCapabilities({
        features: ["frameReferences"],
        entryModes: ["text-to-video", "image-to-video", "init-video"],
        audioSourceKinds: ["Disabled"],
    }),
    boundaryRules: {
        cut: {
            support: "supported",
            code: "wan22.boundary.cut",
            reason: "Decoded WAN Video clips can be joined with a hard cut.",
            constraints: null,
        },
        continue: {
            support: "unsupported",
            code: "wan22.boundary.continue.unsupported",
            reason: "This architecture has no continuity path.",
            constraints: null,
        },
        crossfade: {
            support: "unsupported",
            code: "wan22.boundary.crossfade.unsupported",
            reason: "This architecture has no decoded transition path.",
            constraints: null,
        },
    },
});

/** The WAN 2.2 I2V 14B model as `ArchitectureCatalogSerializer` publishes it. */
export const testWanModelEntry = (): ArchitectureModelEntry => ({
    value: "wan-14b.safetensors",
    label: "WAN 14B",
    architectureId: "wan22",
    modelProfileId: "wan-2.2-i2v-14b",
    modelClassId: "wan-2_2-image2video-14b",
    compatibilityClassId: "wan-21-14b",
    frameGrid: 4,
    frameGridOrigin: 1,
    enhancements: { referencePositions: ["first", "last"] },
    entryModes: ["text-to-video", "image-to-video", "init-video"],
});

/** Mirrors `HostVideoArchitectureModule.Descriptor`. */
export const testHostVideoArchitecture = (): ArchitectureCatalogEntryDto => ({
    id: "host-video",
    label: "Host Video",
    capabilities: testArchitectureCapabilities({
        features: [],
        entryModes: ["text-to-video", "image-to-video", "init-video"],
        audioSourceKinds: ["Disabled"],
    }),
    boundaryRules: {
        cut: {
            support: "supported",
            code: "host-video.boundary.cut",
            reason: "Decoded host videos can be joined with a hard cut.",
            constraints: null,
        },
        continue: {
            support: "unsupported",
            code: "host-video.boundary.continue.unsupported",
            reason: "This architecture has no continuity path.",
            constraints: null,
        },
        crossfade: {
            support: "unsupported",
            code: "host-video.boundary.crossfade.unsupported",
            reason: "This architecture has no decoded transition path.",
            constraints: null,
        },
    },
});

/** A stock-host model the host-video baseline picked up, with no grid to trust. */
export const testHostVideoModelEntry = (): ArchitectureModelEntry => ({
    value: "host-video.safetensors",
    label: "Host Video",
    architectureId: "host-video",
    modelProfileId: "host-video",
    modelClassId: "host-video",
    compatibilityClassId: "host-video",
    frameGrid: 1,
    frameGridOrigin: 1,
    entryModes: ["text-to-video", "image-to-video", "init-video"],
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
                features: [],
                entryModes: ["text-to-video", "image-to-video", "init-video"],
                audioSourceKinds: ["Native"],
            }),
            boundaryRules: {
                cut: {
                    support: "supported",
                    code: "test.boundary.cut",
                    reason: "Only cuts are supported.",
                    constraints: null,
                },
                continue: {
                    support: "unsupported",
                    code: "test.boundary.continue.unsupported",
                    reason: "Continue is unsupported.",
                    constraints: null,
                },
                crossfade: {
                    support: "unsupported",
                    code: "test.boundary.crossfade.unsupported",
                    reason: "Crossfade is unsupported.",
                    constraints: null,
                },
            },
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
            frameGridOrigin: 1,
            entryModes: ["text-to-video", "image-to-video", "init-video"],
        },
    ],
});

/**
 * Every architecture shape authoring has to cope with at once: a fully featured
 * one, a featureless one, and source-only.
 */
export const testCombinedCatalog = (): ArchitectureModelCatalog => {
    const ltx = testArchitectureCatalog();
    const fake = fakeArchitectureCatalog();
    return {
        source: "backend",
        architectures: [
            ...ltx.architectures,
            ...fake.architectures,
            testSourceOnlyArchitecture(),
        ],
        entries: [...ltx.entries, ...fake.entries],
    };
};

export const testCombinedCatalogWithWan = (): ArchitectureModelCatalog => {
    const models = testCombinedCatalog();
    models.architectures.push(testWanArchitecture());
    models.entries.push(testWanModelEntry());
    return models;
};

export const testCombinedCatalogWithHostVideo =
    (): ArchitectureModelCatalog => {
        const models = testCombinedCatalog();
        models.architectures.push(testHostVideoArchitecture());
        models.entries.push(testHostVideoModelEntry());
        return models;
    };

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
    generatedEntryMode: GeneratedEntryMode = "text-to-video",
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
