import {
    AUDIO_SOURCE_DISABLED_KIND,
    AUDIO_SOURCE_NATIVE,
    isAllowedAudioSource,
} from "../../audioSource";
import { AUTHORING_FEATURE_LABELS } from "../generatedFeatures";
import type { ArchitectureCapabilities } from "../types";
import type { AuthoringFeature, ClipCapabilityView } from "./types";

/**
 * Retake re-diffuses a window of existing footage, so a clip with no init video has nothing to
 * retake. The code is the one the backend plan reports for the same condition.
 */
export const RETAKE_SOURCE_RULE = {
    code: "retake-source-required",
    reason: "Retake requires an init-video clip.",
} as const;

/**
 * Clip audio is authorable when the architecture takes a source the user picks. Native and
 * Disabled are the two spellings of "whatever the model does on its own", so neither counts.
 */
export const supportsClipAudio = (
    audioSourceKinds: readonly string[],
): boolean =>
    audioSourceKinds.some(
        (kind) =>
            kind !== AUDIO_SOURCE_DISABLED_KIND && kind !== AUDIO_SOURCE_NATIVE,
    );

export const architectureReason = (
    label: string,
    feature: AuthoringFeature,
): string =>
    `${AUTHORING_FEATURE_LABELS[feature]} is not supported by ${label}.`;

export const noArchitectureReason = (feature: AuthoringFeature): string =>
    `${AUTHORING_FEATURE_LABELS[feature]} requires a generated clip with a known architecture.`;

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

/**
 * The single "does this capability set support feature X" predicate. Capability views,
 * diagnostics, and temporal execution previews all answer through it.
 */
export const architectureFeatureSupport = (
    feature: AuthoringFeature,
    capabilities: ArchitectureCapabilities,
): boolean => capabilities.features.includes(feature);
