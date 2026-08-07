// Generated from ArchitectureFeatureVocabulary.cs. Do not edit by hand.

export const ARCHITECTURE_FEATURE_LABELS = {
    promptRelay: "Prompt relay",
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

/** Every entry mode the serialized catalog can carry, in declaration order. */
export const ENTRY_MODES = [
    "text-to-video",
    "image-to-video",
    "init-video",
] as const;

/** Every boundary join the serialized catalog can carry, in declaration order. */
export const BOUNDARY_MODES = ["cut", "continue", "crossfade"] as const;

/** Every rule support the serialized catalog can carry, in declaration order. */
export const RULE_SUPPORTS = [
    "supported",
    "unsupported",
    "conditional",
] as const;

/** Every continue mode the serialized catalog can carry, in declaration order. */
export const CONTINUE_MODES = ["overlap", "reference"] as const;

/** Every frame reference position the catalog can carry, in declaration order. */
export const FRAME_REFERENCE_POSITIONS = ["first", "last", "any"] as const;

/** Every audio source the catalog can carry, in declaration order. */
export const AUDIO_SOURCE_KINDS = [
    "Disabled",
    "Native",
    "Upload",
    "ControlNet",
    "AceStepFun",
] as const;
