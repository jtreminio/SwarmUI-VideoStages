import {
    audioSourceKind,
    canUseClipLengthFromAudio,
    isAllowedAudioSource,
} from "../audioSource";
import {
    activeStageCount,
    executableBoundaries,
    executableClipIndexes,
} from "../clipSemantics";
import type { Clip } from "../types";
import { hasArchitectureSlotSourcedIcLora } from "./behaviorRegistry";
import { resolvedClipArchitectureId } from "./clipIdentity";
import { effectiveClipCapabilities } from "./modelCapabilities";
import { NONE_ARCHITECTURE_ID } from "./none/identity";
import { createCapabilityViewResolver } from "./policy";
import {
    architectureFeatureSupport,
    supportsClipAudio,
    upscaleModeForMethod,
} from "./policy/featureValues";
import type { AuthoringFeature, CapabilityViewResolver } from "./policy/types";
import type {
    ArchitectureCapabilities,
    ArchitectureModelCatalog,
} from "./types";

export interface ArchitectureDiagnostic {
    severity: "warning" | "error";
    code: string;
    message: string;
    clipIdx?: number;
}

const issue = (
    code: string,
    message: string,
    clipIdx?: number,
    severity: ArchitectureDiagnostic["severity"] = "error",
): ArchitectureDiagnostic => ({ severity, code, message, clipIdx });

const persistedCapabilityIssues = (
    clip: Clip,
    clipIdx: number,
    architectureId: string,
    capabilities: ArchitectureCapabilities,
): ArchitectureDiagnostic[] => {
    const diagnostics: ArchitectureDiagnostic[] = [];
    const supports = (feature: AuthoringFeature): boolean =>
        architectureFeatureSupport(feature, capabilities);
    const unsupported = (
        active: boolean,
        key: string,
        label: string,
        severity?: ArchitectureDiagnostic["severity"],
    ): void => {
        if (active) {
            const effectiveSeverity = severity ?? "warning";
            diagnostics.push(
                issue(
                    `architecture.unsupported.${key}`,
                    effectiveSeverity === "warning"
                        ? `${label} is saved on Clip ${clipIdx}, but its architecture cannot use it. Generation will ignore it and keep the authored setting.`
                        : `${label} is persisted on Clip ${clipIdx}, but its architecture does not support it. Remove it or explicitly convert the clip.`,
                    clipIdx,
                    effectiveSeverity,
                ),
            );
        }
    };
    unsupported(
        !supports("frameReferences") && clip.refs.length > 0,
        "frame-references",
        "Frame references",
    );
    unsupported(
        !supports("referenceFraming") && clip.refFraming !== "crop",
        "reference-framing",
        "Reference framing",
    );
    unsupported(
        !supports("icLora") && clip.icLoras.length > 0,
        "ic-lora",
        "IC-LoRA",
    );
    unsupported(
        !supports("retake") && clip.retake !== null,
        "retake",
        "Retake",
    );
    unsupported(
        !supports("promptRelay") && clip.promptWindows.length > 0,
        "prompt-relay",
        "Prompt relay",
    );
    const activeUpscaleModes = clip.stages
        .filter((stage) => stage.upscale !== 1)
        .map((stage) => upscaleModeForMethod(stage.upscaleMethod));
    if (activeUpscaleModes.includes("unsupported")) {
        diagnostics.push(
            issue(
                "architecture.unsupported.upscale",
                `Stage upscaling is persisted on Clip ${clipIdx}, but its upscale method is not a known method. Remove it or choose a known method.`,
                clipIdx,
            ),
        );
    }
    unsupported(
        !supports("latentUpscale") && activeUpscaleModes.includes("latent"),
        "latent-upscale",
        "Latent interpolation upscaling",
    );
    unsupported(
        !supports("latentModelUpscale") &&
            activeUpscaleModes.includes("latent-model"),
        "latent-model-upscale",
        "Latent-model upscaling",
    );
    const sourceKind = audioSourceKind(clip.audioSource);
    const clipAudioCapabilitySupported = supportsClipAudio(
        capabilities.audioSourceKinds,
    );
    const standaloneAudioSupported =
        capabilities.audioSourceKinds.includes("Native");
    const selectedAudioSourceSupported = isAllowedAudioSource(
        capabilities.audioSourceKinds,
        clip.audioSource,
    );
    unsupported(
        !supports("audioReuse") && clip.reuseAudio,
        "audio-reuse",
        "Captured stage audio reuse",
    );
    unsupported(
        !supports("audioDerivedDuration") && clip.clipLengthFromAudio,
        "audio-derived-duration",
        "Audio-derived clip duration",
    );
    // The control signal is IC-LoRA media, so its duration rides on that feature.
    const supportsControlSignalDerivedDuration = supports("icLora");
    unsupported(
        !supportsControlSignalDerivedDuration && clip.clipLengthFromControlNet,
        "control-signal-derived-duration",
        "Control-signal-derived clip duration",
    );
    unsupported(
        !selectedAudioSourceSupported &&
            (sourceKind !== "Native" || clip.uploadedAudio !== null),
        "audio-source",
        `Audio source '${sourceKind}'`,
        clipAudioCapabilitySupported ? "error" : undefined,
    );
    unsupported(
        clip.saveAudioTrack && !standaloneAudioSupported,
        "audio-output",
        "Standalone audio output",
    );
    // Normalization preserves both length flags as authored, so the clip's own
    // state is what makes an authored flag unusable here.
    if (
        clip.clipLengthFromAudio &&
        supports("audioDerivedDuration") &&
        selectedAudioSourceSupported &&
        !canUseClipLengthFromAudio(clip.audioSource)
    ) {
        diagnostics.push(
            issue(
                "architecture.unusable.clip-length-from-audio",
                `Clip length from audio is persisted on Clip ${clipIdx}, but audio source '${sourceKind}' cannot supply a length. Turn it off or pick a source that can.`,
                clipIdx,
            ),
        );
    }
    if (
        clip.clipLengthFromControlNet &&
        supportsControlSignalDerivedDuration &&
        !hasArchitectureSlotSourcedIcLora(architectureId, clip.icLoras)
    ) {
        diagnostics.push(
            issue(
                "architecture.unusable.clip-length-from-control-net",
                `Clip length from the control signal is persisted on Clip ${clipIdx}, but no IC-LoRA supplies one. Turn it off or add a slot-init-video IC-LoRA.`,
                clipIdx,
            ),
        );
    }
    return diagnostics;
};

/** Resolves the architecture frontend diagnostics may execute, without rewriting authored hints. */
export const effectiveArchitectureIdForClip = (
    clip: Clip,
    catalog: ArchitectureModelCatalog,
): string => resolvedClipArchitectureId(clip, catalog) ?? "unsupported";

/**
 * Validates persisted architecture identity without normalizing it away.
 * Every authored stage is checked, including skipped stages.
 */
export const deriveArchitectureDiagnostics = (
    clips: readonly Clip[],
    catalog: ArchitectureModelCatalog,
    capabilityViews?: CapabilityViewResolver,
): ArchitectureDiagnostic[] => {
    const diagnostics: ArchitectureDiagnostic[] = [];
    const architectureById = new Map(
        catalog.architectures.map((entry) => [entry.id, entry]),
    );
    const modelByName = new Map(
        catalog.entries.map((entry) => [entry.value, entry]),
    );
    const executableClipIndexSet = new Set(executableClipIndexes(clips));
    const resolver = capabilityViews ?? createCapabilityViewResolver(catalog);

    clips.forEach((clip, clipIdx) => {
        const temporalGrid = resolver.forClip(clip).frameGridResolution;
        if (
            executableClipIndexSet.has(clipIdx) &&
            temporalGrid.status === "conflict"
        ) {
            diagnostics.push(
                issue(
                    "architecture.temporal-grid-conflict",
                    `Clip ${clipIdx}'s active model handlers have no representable compatible temporal grid. Use stage models with compatible temporal requirements.`,
                    clipIdx,
                ),
            );
        }
        const sourceOnly =
            activeStageCount(clip) === 0 && clip.initVideo !== null;
        const resolvedFirstModel =
            !sourceOnly && clip.stages.length > 0
                ? modelByName.get(clip.stages[0].model)
                : undefined;
        const effectiveArchitectureId = effectiveArchitectureIdForClip(
            clip,
            catalog,
        );
        const effectiveModelProfileId =
            resolvedFirstModel?.modelProfileId ?? clip.modelProfileId;
        const effectiveCompatibilityClassId =
            resolvedFirstModel?.compatibilityClassId ?? null;
        if (
            resolvedFirstModel?.architectureId &&
            resolvedFirstModel.architectureId !== clip.architectureHint
        ) {
            diagnostics.push(
                issue(
                    "architecture.stale-identity-hint",
                    `Clip ${clipIdx} caches architecture hint '${clip.architectureHint}', but model '${clip.stages[0].model}' resolves to '${resolvedFirstModel.architectureId}'. Generation uses the resolved architecture and preserves the authored hint.`,
                    clipIdx,
                    "warning",
                ),
            );
        }
        if (
            resolvedFirstModel?.modelProfileId &&
            resolvedFirstModel.modelProfileId !== clip.modelProfileId
        ) {
            diagnostics.push(
                issue(
                    "architecture.stale-profile-hint",
                    `Clip ${clipIdx} caches model profile '${clip.modelProfileId}', but model '${clip.stages[0].model}' resolves to '${resolvedFirstModel.modelProfileId}'. Generation uses the resolved profile and preserves the authored hint.`,
                    clipIdx,
                    "warning",
                ),
            );
        }
        if (sourceOnly) {
            if (
                clip.architectureHint !== NONE_ARCHITECTURE_ID ||
                clip.modelProfileId !== NONE_ARCHITECTURE_ID
            ) {
                diagnostics.push(
                    issue(
                        "architecture.source-only-requires-none",
                        `Source-only Clip ${clipIdx} must use architecture and profile 'none'.`,
                        clipIdx,
                    ),
                );
            }
        }

        const architecture = sourceOnly
            ? architectureById.get("none")
            : architectureById.get(effectiveArchitectureId);
        if (architecture) {
            const capabilities = effectiveClipCapabilities(
                clip,
                architecture,
                (model) => modelByName.get(model),
            );
            if (capabilities) {
                diagnostics.push(
                    ...persistedCapabilityIssues(
                        clip,
                        clipIdx,
                        effectiveArchitectureId,
                        capabilities,
                    ),
                );
            }
        }

        let dormantArchitecture: string | null = null;
        let dormantCompatibilityClass: string | null = null;
        clip.stages.forEach((stage, stageIdx) => {
            const resolved = modelByName.get(stage.model);
            if (!resolved?.architectureId || !resolved.modelProfileId) {
                diagnostics.push(
                    issue(
                        "architecture.model-unknown",
                        `Clip ${clipIdx} Stage ${stageIdx} model '${stage.model}' is not in the architecture catalog.`,
                        clipIdx,
                    ),
                );
                return;
            }
            if (sourceOnly && dormantArchitecture === null) {
                dormantArchitecture = resolved.architectureId;
                dormantCompatibilityClass = resolved.compatibilityClassId;
            }
            const mixedDormant =
                sourceOnly &&
                dormantArchitecture !== null &&
                (resolved.architectureId !== dormantArchitecture ||
                    resolved.compatibilityClassId !==
                        dormantCompatibilityClass);
            if (
                mixedDormant ||
                (!sourceOnly &&
                    (resolved.architectureId !== effectiveArchitectureId ||
                        (effectiveCompatibilityClassId !== null &&
                            resolved.compatibilityClassId !==
                                effectiveCompatibilityClassId)))
            ) {
                diagnostics.push(
                    issue(
                        "architecture.mixed-stage",
                        sourceOnly
                            ? `Source-only Clip ${clipIdx} has dormant stages from incompatible model families; Stage ${stageIdx}${stage.skipped ? " (skipped)" : ""} resolves to architecture '${resolved.architectureId}' and compatibility class '${resolved.compatibilityClassId}'.`
                            : `Clip ${clipIdx} uses architecture '${effectiveArchitectureId}' and compatibility class '${effectiveCompatibilityClassId}', but Stage ${stageIdx}${stage.skipped ? " (skipped)" : ""} resolves to '${resolved.architectureId}' and '${resolved.compatibilityClassId}'.`,
                        clipIdx,
                    ),
                );
            }
            if (
                stage.modelProfileId !== resolved.modelProfileId ||
                (!sourceOnly &&
                    stageIdx === 0 &&
                    effectiveModelProfileId !== resolved.modelProfileId)
            ) {
                diagnostics.push(
                    issue(
                        "architecture.profile-mismatch",
                        `Clip ${clipIdx} Stage ${stageIdx} caches a profile identity that does not match model '${stage.model}'. Generation uses the resolved profile and preserves the authored hint.`,
                        clipIdx,
                        "warning",
                    ),
                );
            }
        });
    });

    for (const seam of executableBoundaries(clips)) {
        const left = { clip: clips[seam.leftIdx], clipIdx: seam.leftIdx };
        const right = { clip: clips[seam.rightIdx], clipIdx: seam.rightIdx };
        if (
            effectiveArchitectureIdForClip(left.clip, catalog) !==
                effectiveArchitectureIdForClip(right.clip, catalog) &&
            left.clip.boundaryOut !== "cut"
        ) {
            diagnostics.push(
                issue(
                    "architecture.cross-boundary-cut-only",
                    `Clip ${left.clipIdx} → ${right.clipIdx} crosses architectures; '${left.clip.boundaryOut}' remains authored, but generation safely uses a cut.`,
                    left.clipIdx,
                    "warning",
                ),
            );
            continue;
        }
        const boundary = resolver.forBoundary(
            left.clip,
            right.clip,
            left.clipIdx,
            right.clipIdx,
        );
        if (
            boundary.effective(left.clip.boundaryOut) !== left.clip.boundaryOut
        ) {
            const reason = boundary.reason
                ? ` ${boundary.reason}`
                : ` Its requested value is preserved for repair, but only '${boundary.effective(left.clip.boundaryOut)}' can execute.`;
            diagnostics.push(
                issue(
                    "architecture.boundary-unsupported",
                    `Clip ${left.clipIdx} cannot execute a '${left.clip.boundaryOut}' boundary into Clip ${right.clipIdx}.${reason} Generation safely uses a cut while preserving the authored boundary.`,
                    left.clipIdx,
                    "warning",
                ),
            );
        }
    }
    return diagnostics;
};
