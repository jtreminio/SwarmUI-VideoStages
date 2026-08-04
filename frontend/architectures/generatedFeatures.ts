// Generated from ArchitectureFeatureVocabulary.cs. Do not edit by hand.

export const AUTHORING_FEATURE_LABELS = {
    promptRelay: "Relay prompts",
    frameReferences: "Frame references",
    referenceFraming: "Reference framing",
    retake: "Retake",
    audioBoundaryCarry: "Boundary audio carry",
    latentUpscale: "Latent interpolation upscaling",
    latentModelUpscale: "Latent-model upscaling",
    audioReuse: "Captured stage audio reuse",
    audioDerivedDuration: "Audio-derived clip duration",
    icLora: "IC-LoRA",
} as const;

export type GeneratedAuthoringFeature = keyof typeof AUTHORING_FEATURE_LABELS;
