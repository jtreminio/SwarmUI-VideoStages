import { isAllowedAudioSource } from "../../audioSource";
import {
    AUTHORING_FEATURE_LABELS,
    AUTHORING_FEATURE_WIRE_NAMES,
} from "../generatedFeatures";
import type { ArchitectureCapabilities } from "../types";
import type { AuthoringFeature, ClipCapabilityView } from "./types";

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

export interface FeatureSupportScope {
    capabilities: ArchitectureCapabilities;
    /** Persisted audio source, when the caller needs the value checked too. */
    audioSource?: string;
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
    if (!capability.features.includes(AUTHORING_FEATURE_WIRE_NAMES[feature])) {
        return false;
    }
    if (feature === "clipAudio" && scope.audioSource !== undefined) {
        return isAllowedAudioSource(
            capability.audioSourceKinds,
            scope.audioSource,
        );
    }
    return true;
};
