import { isAllowedAudioSource } from "../../audioSource";
import type { ArchitectureCapabilities } from "../types";
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

export type UpscaleMethodMode =
    | "pixel"
    | "model"
    | "latent"
    | "latent-model"
    | "unsupported";

export const upscaleModeForMethod = (method: string): UpscaleMethodMode => {
    const normalized = method.trim().toLowerCase();
    const hasMethodName = (prefix: string): boolean =>
        normalized.startsWith(prefix) &&
        normalized.slice(prefix.length).trim().length > 0;
    if (hasMethodName("latentmodel-")) return "latent-model";
    if (hasMethodName("latent-")) return "latent";
    if (hasMethodName("pixel-")) return "pixel";
    if (hasMethodName("model-")) return "model";
    return "unsupported";
};

export interface FeatureSupportScope {
    capabilities: ArchitectureCapabilities;
    /** Canonical flat feature set. Scoped capability arrays remain migration aliases. */
    extras?: readonly string[];
    /** Persisted audio source, when the caller needs the value checked too. */
    audioSource?: string;
    /** Persisted upscale method, when the caller needs the mode checked too. */
    upscaleMethod?: string;
}

/**
 * The single "does this capability set support feature X" predicate. Capability views,
 * diagnostics, and temporal execution previews all answer through it.
 */
export const architectureFeatureSupport = (
    feature: AuthoringFeature,
    scope: FeatureSupportScope,
): boolean => {
    const capability = scope.capabilities;
    const extras = scope.extras;
    const has = (
        extra: string,
        legacy: readonly string[],
        legacyValue: string = extra,
    ): boolean =>
        extras === undefined
            ? legacy.includes(legacyValue)
            : extras.includes(extra);
    switch (feature) {
        case "multiStage":
            return has("multi-stage", capability.architecture);
        case "sourceVideo":
            return has("source-video", capability.clip);
        case "frameReferences":
            return has("frame-references", capability.stage);
        case "referenceFraming":
            return has("reference-framing", capability.clip);
        case "retake":
            return has("retake", capability.clip);
        case "majorPrompt":
            return has("prompts", capability.clip);
        case "promptRelay":
            return has("prompt-relay", capability.clip);
        case "clipAudio":
            return (
                has("audio-sources", capability.clip) &&
                (scope.audioSource === undefined ||
                    isAllowedAudioSource(
                        capability.audioSourceKinds,
                        scope.audioSource,
                    ))
            );
        case "audioReuse":
            return has("audio-reuse", capability.clip);
        case "audioDerivedDuration":
            return has("audio-derived-duration", capability.clip);
        case "controlSignalDerivedDuration":
            return has("control-signal-derived-duration", capability.clip);
        case "stageLoras":
            return has("lora", capability.stage);
        case "icLora":
            return has("ic-lora", capability.stage);
        case "hdr":
            return has("hdr", capability.stage);
        case "upscale":
            return scope.upscaleMethod === undefined
                ? ["pixel", "model", "latent", "latent-model"].some((mode) =>
                      has(`${mode}-upscale`, capability.upscaleModes, mode),
                  )
                : has(
                      `${upscaleModeForMethod(scope.upscaleMethod)}-upscale`,
                      capability.upscaleModes,
                      upscaleModeForMethod(scope.upscaleMethod),
                  );
    }
};
