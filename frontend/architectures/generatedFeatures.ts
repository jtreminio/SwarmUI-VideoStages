// Generated from ArchitectureFeatureVocabulary.cs. Do not edit by hand.

export const AUTHORING_FEATURE_LABELS = {
    promptRelay: "Relay prompts",
    frameReferences: "Frame references",
    referenceFraming: "Reference framing",
    retake: "Retakes",
    audioSegments: "Audio segments",
    audioReuse: "Captured stage audio reuse",
    audioDerivedDuration: "Audio-derived clip duration",
    icLora: "IC-LoRA",
} as const;

export type GeneratedAuthoringFeature = keyof typeof AUTHORING_FEATURE_LABELS;
