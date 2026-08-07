// Generated from ArchitectureFeatureVocabulary.cs. Do not edit by hand.

export const ARCHITECTURE_FEATURE_LABELS = {
    promptRelay: "Relay prompts",
    frameReferences: "Frame references",
    clipReferences: "Clip references",
    referenceFraming: "Reference framing",
    retake: "Retake",
    audioBoundaryCarry: "Boundary audio carry",
    latentUpscale: "Latent interpolation upscaling",
    latentModelUpscale: "Latent-model upscaling",
    audioReuse: "Captured stage audio reuse",
    audioDerivedDuration: "Audio-derived clip duration",
    icLora: "IC-LoRA",
} as const;

export type GeneratedArchitectureFeature =
    keyof typeof ARCHITECTURE_FEATURE_LABELS;

/** The entry modes a generated clip can take; a sourced clip is always init-video. */
export type GeneratedEntryMode = "text-to-video" | "image-to-video";
