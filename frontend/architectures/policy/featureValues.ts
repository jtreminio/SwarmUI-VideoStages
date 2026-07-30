import { isAllowedAudioSource } from "../../audioSource";
import {
    AUTHORING_FEATURE_CAPABILITIES,
    AUTHORING_FEATURE_LABELS,
    doesAuthoringFeatureRequireEveryCapability,
    type GeneratedAuthoringFeatureCapability,
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
    /** Additive flat capability aliases/enhancements from the architecture or resolved model. */
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
    const supports = (
        binding: GeneratedAuthoringFeatureCapability,
    ): boolean => {
        const [capabilityScope, wireName, upscaleMode] = binding;
        const typedCapability =
            upscaleMode === null
                ? capability[capabilityScope].includes(wireName)
                : capability.upscaleModes.includes(upscaleMode);
        return typedCapability || scope.extras?.includes(wireName) === true;
    };

    let bindings = AUTHORING_FEATURE_CAPABILITIES[feature];
    if (feature === "upscale" && scope.upscaleMethod !== undefined) {
        const requestedMode = upscaleModeForMethod(scope.upscaleMethod);
        bindings = bindings.filter(
            ([, , upscaleMode]) => upscaleMode === requestedMode,
        );
    }
    const supported = doesAuthoringFeatureRequireEveryCapability(feature)
        ? bindings.every(supports)
        : bindings.some(supports);
    if (!supported) {
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
