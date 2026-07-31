// Generated from ArchitectureFeatureVocabulary.cs. Do not edit by hand.

export const CAPABILITY_WIRE_NAMES = {
    architecture: {
        generatedEntry: "generated-entry",
        initVideoEntry: "init-video-entry",
        nativeAudio: "native-audio",
    },
    clip: {
        initVideo: "init-video",
        prompts: "prompts",
        promptRelay: "prompt-relay",
        references: "references",
        referenceFraming: "reference-framing",
        retake: "retake",
        audioSources: "audio-sources",
        audioSegments: "audio-segments",
        audioReuse: "audio-reuse",
        audioDerivedDuration: "audio-derived-duration",
        controlSignalDerivedDuration: "control-signal-derived-duration",
    },
    stage: {
        imageInput: "image-input",
        videoInput: "video-input",
        pixelUpscale: "pixel-upscale",
        modelUpscale: "model-upscale",
        latentUpscale: "latent-upscale",
        latentModelUpscale: "latent-model-upscale",
        lora: "lora",
        icLora: "ic-lora",
        frameReferences: "frame-references",
    },
} as const;

export type CapabilityVocabularyScope = keyof typeof CAPABILITY_WIRE_NAMES;

export type GeneratedAuthoringFeatureCapability = readonly [
    scope: CapabilityVocabularyScope,
    wireName: string,
    upscaleMode: string | null,
];

export const AUTHORING_FEATURES = [
    "initVideo",
    "frameReferences",
    "referenceFraming",
    "retake",
    "majorPrompt",
    "promptRelay",
    "clipAudio",
    "audioReuse",
    "audioDerivedDuration",
    "controlSignalDerivedDuration",
    "stageLoras",
    "icLora",
    "upscale",
] as const;

export type GeneratedAuthoringFeature = (typeof AUTHORING_FEATURES)[number];

export const IGNORED_WHEN_UNSUPPORTED_FEATURES = [
    "frameReferences",
    "referenceFraming",
    "retake",
    "promptRelay",
    "clipAudio",
    "audioReuse",
    "audioDerivedDuration",
    "controlSignalDerivedDuration",
    "stageLoras",
    "icLora",
    "upscale",
] as const satisfies readonly GeneratedAuthoringFeature[];

export const AUTHORING_FEATURES_REQUIRING_EVERY_CAPABILITY = [
    "frameReferences",
] as const satisfies readonly GeneratedAuthoringFeature[];

export const doesAuthoringFeatureRequireEveryCapability = (
    feature: GeneratedAuthoringFeature,
): boolean =>
    (
        AUTHORING_FEATURES_REQUIRING_EVERY_CAPABILITY as readonly string[]
    ).includes(feature);

export const isIgnoredWhenUnsupportedFeature = (
    feature: GeneratedAuthoringFeature,
): boolean =>
    (IGNORED_WHEN_UNSUPPORTED_FEATURES as readonly string[]).includes(feature);

export const AUTHORING_FEATURE_LABELS: Record<
    GeneratedAuthoringFeature,
    string
> = {
    initVideo: "Source video",
    frameReferences: "Frame references",
    referenceFraming: "Reference framing",
    retake: "Retakes",
    majorPrompt: "Major prompts",
    promptRelay: "Relay prompts",
    clipAudio: "Clip audio",
    audioReuse: "Captured stage audio reuse",
    audioDerivedDuration: "Audio-derived clip duration",
    controlSignalDerivedDuration: "Control-signal-derived clip duration",
    stageLoras: "LoRAs",
    icLora: "IC-LoRA",
    upscale: "Stage upscaling",
};

export const AUTHORING_FEATURE_CAPABILITIES: Record<
    GeneratedAuthoringFeature,
    readonly GeneratedAuthoringFeatureCapability[]
> = {
    initVideo: [["clip", CAPABILITY_WIRE_NAMES.clip.initVideo, null]],
    frameReferences: [
        ["clip", CAPABILITY_WIRE_NAMES.clip.references, null],
        ["stage", CAPABILITY_WIRE_NAMES.stage.frameReferences, null],
    ],
    referenceFraming: [
        ["clip", CAPABILITY_WIRE_NAMES.clip.referenceFraming, null],
    ],
    retake: [["clip", CAPABILITY_WIRE_NAMES.clip.retake, null]],
    majorPrompt: [["clip", CAPABILITY_WIRE_NAMES.clip.prompts, null]],
    promptRelay: [["clip", CAPABILITY_WIRE_NAMES.clip.promptRelay, null]],
    clipAudio: [["clip", CAPABILITY_WIRE_NAMES.clip.audioSources, null]],
    audioReuse: [["clip", CAPABILITY_WIRE_NAMES.clip.audioReuse, null]],
    audioDerivedDuration: [
        ["clip", CAPABILITY_WIRE_NAMES.clip.audioDerivedDuration, null],
    ],
    controlSignalDerivedDuration: [
        ["clip", CAPABILITY_WIRE_NAMES.clip.controlSignalDerivedDuration, null],
    ],
    stageLoras: [["stage", CAPABILITY_WIRE_NAMES.stage.lora, null]],
    icLora: [["stage", CAPABILITY_WIRE_NAMES.stage.icLora, null]],
    upscale: [
        ["stage", CAPABILITY_WIRE_NAMES.stage.pixelUpscale, "pixel"],
        ["stage", CAPABILITY_WIRE_NAMES.stage.modelUpscale, "model"],
        ["stage", CAPABILITY_WIRE_NAMES.stage.latentUpscale, "latent"],
        [
            "stage",
            CAPABILITY_WIRE_NAMES.stage.latentModelUpscale,
            "latent-model",
        ],
    ],
};

export const CONDITIONAL_RULE_CODES = {
    audioReuseRequiresStages: "audio.reuse.requires_three_stages",
    normalLoraRequiresSamplingStage: "normal-lora-requires-sampling-stage",
    promptRelayRequiresFixedLength: "prompt-relay-dynamic-length-unsupported",
    retakeExcludesReferences: "retake-frame-references-unsupported",
    retakeRequiresSource: "retake-source-required",
} as const;

export type GeneratedConditionalRuleCode =
    (typeof CONDITIONAL_RULE_CODES)[keyof typeof CONDITIONAL_RULE_CODES];
