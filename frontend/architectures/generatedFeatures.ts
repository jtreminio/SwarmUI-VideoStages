// Generated from ArchitectureFeatureVocabulary.cs. Do not edit by hand.

export const CAPABILITY_WIRE_NAMES = {
    clip: {
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
        icLora: "ic-lora",
        frameReferences: "frame-references",
    },
} as const;

export type CapabilityVocabularyScope = keyof typeof CAPABILITY_WIRE_NAMES;

export type GeneratedAuthoringFeatureCapability = readonly [
    scope: CapabilityVocabularyScope,
    wireName: string,
];

export const AUTHORING_FEATURES = [
    "frameReferences",
    "referenceFraming",
    "retake",
    "promptRelay",
    "clipAudio",
    "audioReuse",
    "audioDerivedDuration",
    "controlSignalDerivedDuration",
    "icLora",
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
    "icLora",
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
    frameReferences: "Frame references",
    referenceFraming: "Reference framing",
    retake: "Retakes",
    promptRelay: "Relay prompts",
    clipAudio: "Clip audio",
    audioReuse: "Captured stage audio reuse",
    audioDerivedDuration: "Audio-derived clip duration",
    controlSignalDerivedDuration: "Control-signal-derived clip duration",
    icLora: "IC-LoRA",
};

export const AUTHORING_FEATURE_CAPABILITIES: Record<
    GeneratedAuthoringFeature,
    readonly GeneratedAuthoringFeatureCapability[]
> = {
    frameReferences: [
        ["clip", CAPABILITY_WIRE_NAMES.clip.references],
        ["stage", CAPABILITY_WIRE_NAMES.stage.frameReferences],
    ],
    referenceFraming: [["clip", CAPABILITY_WIRE_NAMES.clip.referenceFraming]],
    retake: [["clip", CAPABILITY_WIRE_NAMES.clip.retake]],
    promptRelay: [["clip", CAPABILITY_WIRE_NAMES.clip.promptRelay]],
    clipAudio: [["clip", CAPABILITY_WIRE_NAMES.clip.audioSources]],
    audioReuse: [["clip", CAPABILITY_WIRE_NAMES.clip.audioReuse]],
    audioDerivedDuration: [
        ["clip", CAPABILITY_WIRE_NAMES.clip.audioDerivedDuration],
    ],
    controlSignalDerivedDuration: [
        ["clip", CAPABILITY_WIRE_NAMES.clip.controlSignalDerivedDuration],
    ],
    icLora: [["stage", CAPABILITY_WIRE_NAMES.stage.icLora]],
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
