import {
    AUDIO_SOURCE_DISABLED_KIND,
    AUDIO_SOURCE_NATIVE,
    isAllowedAudioSource,
} from "../../audioSource";
import { PLAN_DIAGNOSTIC_RETAKE_SOURCE_REQUIRED } from "../../generatedPlanDiagnostics";
import {
    UPSCALE_METHOD_PREFIXES,
    UPSCALE_MODE_UNSUPPORTED,
} from "../../generatedUpscaleModes";
import {
    ARCHITECTURE_FEATURE_LABELS,
    type GeneratedArchitectureFeature,
} from "../generatedFeatures";
import type { ArchitectureCapabilities } from "../types";
import type { ClipCapabilityView } from "./types";

/**
 * Retake re-diffuses a window of existing footage, so a clip with no init video has nothing to
 * retake. The frontend refuses it up front and the backend reports the same condition after the
 * fact, under the code they share.
 */
export const RETAKE_SOURCE_RULE = {
    code: PLAN_DIAGNOSTIC_RETAKE_SOURCE_REQUIRED,
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
    feature: GeneratedArchitectureFeature,
): string =>
    `${ARCHITECTURE_FEATURE_LABELS[feature]} is not supported by ${label}.`;

export const noArchitectureReason = (
    feature: GeneratedArchitectureFeature,
): string =>
    `${ARCHITECTURE_FEATURE_LABELS[feature]} requires a generated clip with a known architecture.`;

export const isAudioSourceSupported = (
    view: ClipCapabilityView,
    source: string,
): boolean => isAllowedAudioSource(view.audioSourceKinds, source);

export type UpscaleMethodMode =
    | (typeof UPSCALE_METHOD_PREFIXES)[number][1]
    | typeof UPSCALE_MODE_UNSUPPORTED;

export const upscaleModeForMethod = (method: string): UpscaleMethodMode => {
    const normalized = method.trim().toLowerCase();
    for (const [prefix, mode] of UPSCALE_METHOD_PREFIXES) {
        if (
            normalized.startsWith(prefix) &&
            normalized.slice(prefix.length).trim().length > 0
        ) {
            return mode;
        }
    }
    return UPSCALE_MODE_UNSUPPORTED;
};

/**
 * The single "does this capability set support feature X" predicate. Capability views,
 * diagnostics, and temporal execution previews all answer through it.
 */
export const architectureFeatureSupport = (
    feature: GeneratedArchitectureFeature,
    capabilities: ArchitectureCapabilities,
): boolean => capabilities.features.includes(feature);
