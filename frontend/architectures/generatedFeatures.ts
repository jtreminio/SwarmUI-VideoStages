// Generated from ArchitectureFeatureVocabulary.cs. Do not edit by hand.

export const AUTHORING_FEATURE_WIRE_NAMES = {
    promptRelay: "prompt-relay",
    frameReferences: "frame-references",
    referenceFraming: "reference-framing",
    retake: "retake",
    clipAudio: "audio-sources",
    audioSegments: "audio-segments",
    audioReuse: "audio-reuse",
    audioDerivedDuration: "audio-derived-duration",
    controlSignalDerivedDuration: "control-signal-derived-duration",
    icLora: "ic-lora",
} as const;

export type GeneratedAuthoringFeature =
    keyof typeof AUTHORING_FEATURE_WIRE_NAMES;

export const AUTHORING_FEATURE_LABELS: Record<
    GeneratedAuthoringFeature,
    string
> = {
    promptRelay: "Relay prompts",
    frameReferences: "Frame references",
    referenceFraming: "Reference framing",
    retake: "Retakes",
    clipAudio: "Clip audio",
    audioSegments: "Audio segments",
    audioReuse: "Captured stage audio reuse",
    audioDerivedDuration: "Audio-derived clip duration",
    controlSignalDerivedDuration: "Control-signal-derived clip duration",
    icLora: "IC-LoRA",
};

export const CONDITIONAL_RULE_CODES = {
    retakeRequiresSource: "retake-source-required",
} as const;

export type GeneratedConditionalRuleCode =
    (typeof CONDITIONAL_RULE_CODES)[keyof typeof CONDITIONAL_RULE_CODES];
