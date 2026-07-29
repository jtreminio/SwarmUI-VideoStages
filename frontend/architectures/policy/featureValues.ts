import { isAllowedAudioSource } from "../../audioSource";
import type { AuthoringFeature, ClipCapabilityView } from "./types";

const FEATURE_LABEL: Record<AuthoringFeature, string> = {
    multiStage: "Multiple stages",
    sourceVideo: "Source video",
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
    hdr: "HDR",
    upscale: "Stage upscaling",
};

export const architectureReason = (
    label: string,
    feature: AuthoringFeature,
): string => `${FEATURE_LABEL[feature]} is not supported by ${label}.`;

export const noArchitectureReason = (feature: AuthoringFeature): string =>
    `${FEATURE_LABEL[feature]} requires a generated clip with a known architecture.`;

export const isAudioSourceSupported = (
    view: ClipCapabilityView,
    source: string,
): boolean => isAllowedAudioSource(view.audioSourceKinds, source);

export const upscaleModeForMethod = (method: string): string => {
    const normalized = method.trim().toLowerCase();
    if (normalized.startsWith("latentmodel-")) return "latent-model";
    if (normalized.startsWith("latent-")) return "latent";
    if (normalized.startsWith("pixel-")) return "pixel";
    return "model";
};
