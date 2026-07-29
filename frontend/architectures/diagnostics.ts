import { audioSourceKind, canUseClipLengthFromAudio } from "../audioSource";
import {
    activeStageCount,
    executableBoundaries,
    executableClipIndexes,
} from "../clipSemantics";
import type { Clip } from "../types";
import {
    hasArchitectureSlotSourcedIcLora,
    isArchitectureHdrFeature,
} from "./behaviorRegistry";
import {
    CONDITIONAL_RULE_CODES,
    conditionalRule,
    evaluateConditionalRule,
} from "./conditionalRules";
import { architectureSupportsClipStart } from "./conversion/entryModePolicy";
import { NONE_ARCHITECTURE_ID } from "./none/identity";
import { createBoundaryCapabilityViews } from "./policy/boundaryPolicy";
import {
    architectureFeatureSupport,
    createClipStageCapabilityViews,
} from "./policy/clipStageViews";
import type { AuthoringFeature } from "./policy/types";
import type {
    ArchitectureCapabilities,
    ArchitectureModelCatalog,
} from "./types";

export interface ArchitectureDiagnostic {
    severity: "error";
    code: string;
    message: string;
    clipIdx?: number;
}

const issue = (
    code: string,
    message: string,
    clipIdx?: number,
): ArchitectureDiagnostic => ({ severity: "error", code, message, clipIdx });

const persistedCapabilityIssues = (
    clip: Clip,
    clipIdx: number,
    capabilities: ArchitectureCapabilities,
): ArchitectureDiagnostic[] => {
    const diagnostics: ArchitectureDiagnostic[] = [];
    const supports = (
        feature: AuthoringFeature,
        value?: { audioSource?: string; upscaleMethod?: string },
    ): boolean =>
        architectureFeatureSupport(feature, { capabilities, ...value });
    const unsupported = (active: boolean, key: string, label: string): void => {
        if (active) {
            diagnostics.push(
                issue(
                    `architecture.unsupported.${key}`,
                    `${label} is persisted on Clip ${clipIdx}, but its architecture does not support it. Remove it or explicitly convert the clip.`,
                    clipIdx,
                ),
            );
        }
    };
    unsupported(
        !supports("multiStage") && activeStageCount(clip) > 1,
        "multi-stage",
        "Multiple active stages",
    );
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
        !supports("hdr") &&
            clip.icLoras.some((entry) =>
                isArchitectureHdrFeature(clip.architecture, entry),
            ),
        "hdr",
        "HDR",
    );
    unsupported(
        !supports("retake") && clip.retake !== null,
        "retake",
        "Retake",
    );
    unsupported(
        !supports("majorPrompt") && clip.prompt.trim().length > 0,
        "major-prompt",
        "Major prompt",
    );
    unsupported(
        !supports("sourceVideo") && clip.sourceVideo !== null,
        "source-video",
        "Source video",
    );
    unsupported(
        !supports("promptRelay") && clip.promptWindows.length > 0,
        "prompt-relay",
        "Prompt relay",
    );
    unsupported(
        !supports("stageLoras") &&
            clip.stages.some((stage) =>
                clip.loras.some(
                    (_, index) => (stage.loraWeights[index] ?? 1) !== 0,
                ),
            ),
        "stage-loras",
        "LoRAs",
    );
    unsupported(
        clip.stages.some(
            (stage) =>
                stage.upscale !== 1 &&
                !supports("upscale", { upscaleMethod: stage.upscaleMethod }),
        ),
        "upscale",
        "Stage upscaling",
    );
    const sourceKind = audioSourceKind(clip.audioSource);
    const selectedAudioSourceSupported = supports("clipAudio", {
        audioSource: clip.audioSource,
    });
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
    const supportsControlSignalDerivedDuration = supports(
        "controlSignalDerivedDuration",
    );
    unsupported(
        !supportsControlSignalDerivedDuration && clip.clipLengthFromControlNet,
        "control-signal-derived-duration",
        "Control-signal-derived clip duration",
    );
    unsupported(
        !selectedAudioSourceSupported &&
            (sourceKind !== "Native" ||
                clip.uploadedAudio !== null ||
                clip.saveAudioTrack),
        "audio-source",
        `Audio source '${sourceKind}'`,
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
        !hasArchitectureSlotSourcedIcLora(clip.architecture, clip.icLoras)
    ) {
        diagnostics.push(
            issue(
                "architecture.unusable.clip-length-from-control-net",
                `Clip length from the control signal is persisted on Clip ${clipIdx}, but no IC-LoRA supplies one. Turn it off or add a slot-sourced IC-LoRA.`,
                clipIdx,
            ),
        );
    }
    return diagnostics;
};

/**
 * Validates persisted architecture identity without normalizing it away.
 * Every authored stage is checked, including skipped stages.
 */
export const deriveArchitectureDiagnostics = (
    clips: readonly Clip[],
    catalog: ArchitectureModelCatalog,
    generatedEntryMode: "text-to-video" | "image-to-video" = "text-to-video",
): ArchitectureDiagnostic[] => {
    const diagnostics: ArchitectureDiagnostic[] = [];
    const architectureById = new Map(
        catalog.architectures.map((entry) => [entry.id, entry]),
    );
    const boundaries = createBoundaryCapabilityViews(
        architectureById,
        createClipStageCapabilityViews(architectureById).forClip,
    );
    const modelByName = new Map(
        catalog.entries.map((entry) => [entry.value, entry]),
    );
    const executableClipIndexSet = new Set(executableClipIndexes(clips));

    clips.forEach((clip, clipIdx) => {
        const sourceOnly =
            activeStageCount(clip) === 0 && clip.sourceVideo !== null;
        if (sourceOnly) {
            if (
                clip.architecture !== NONE_ARCHITECTURE_ID ||
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
            : architectureById.get(clip.architecture);
        if (!architecture && !sourceOnly) {
            diagnostics.push(
                issue(
                    "architecture.unknown",
                    `Clip ${clipIdx} uses unknown architecture '${clip.architecture}'. Its persisted settings were preserved, but generation is blocked.`,
                    clipIdx,
                ),
            );
        } else if (architecture) {
            diagnostics.push(
                ...persistedCapabilityIssues(
                    clip,
                    clipIdx,
                    architecture.capabilities,
                ),
            );
            if (
                !sourceOnly &&
                activeStageCount(clip) > 0 &&
                !clip.stages
                    .filter((stage) => !stage.skipped)
                    .every((stage) => {
                        const resolved = modelByName.get(stage.model);
                        const profile = resolved?.architectureId
                            ? architectureById
                                  .get(resolved.architectureId)
                                  ?.profiles.find(
                                      (candidate) =>
                                          candidate.id ===
                                          resolved.modelProfileId,
                                  )
                            : null;
                        return (
                            profile !== null &&
                            profile !== undefined &&
                            architectureSupportsClipStart(
                                profile.entryModes,
                                clip,
                                generatedEntryMode,
                            )
                        );
                    })
            ) {
                diagnostics.push(
                    issue(
                        "architecture.entry-mode-unsupported",
                        `Clip ${clipIdx} cannot start from the current ${generatedEntryMode} host entry with architecture '${architecture.id}'.`,
                        clipIdx,
                    ),
                );
            }
        }

        let dormantArchitecture: string | null = null;
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
            }
            const mixedDormant =
                sourceOnly &&
                dormantArchitecture !== null &&
                resolved.architectureId !== dormantArchitecture;
            if (
                mixedDormant ||
                (!sourceOnly && resolved.architectureId !== clip.architecture)
            ) {
                diagnostics.push(
                    issue(
                        "architecture.mixed-stage",
                        sourceOnly
                            ? `Source-only Clip ${clipIdx} has dormant stages from multiple architectures; Stage ${stageIdx}${stage.skipped ? " (skipped)" : ""} resolves to '${resolved.architectureId}'.`
                            : `Clip ${clipIdx} is locked to '${clip.architecture}', but Stage ${stageIdx}${stage.skipped ? " (skipped)" : ""} resolves to '${resolved.architectureId}'.`,
                        clipIdx,
                    ),
                );
            }
            if (
                stage.modelProfileId !== resolved.modelProfileId ||
                (!sourceOnly &&
                    stageIdx === 0 &&
                    clip.modelProfileId !== resolved.modelProfileId)
            ) {
                diagnostics.push(
                    issue(
                        "architecture.profile-mismatch",
                        `Clip ${clipIdx} Stage ${stageIdx} profile identity does not match model '${stage.model}'.`,
                        clipIdx,
                    ),
                );
            }
            const resolvedProfile = architectureById
                .get(resolved.architectureId)
                ?.profiles.find(
                    (profile) => profile.id === resolved.modelProfileId,
                );
            const hasEffectiveNormalLora = clip.loras.some(
                (_, index) => (stage.loraWeights[index] ?? 1) !== 0,
            );
            if (
                hasEffectiveNormalLora &&
                resolvedProfile &&
                !resolvedProfile.capabilities.includes("normal-lora")
            ) {
                diagnostics.push(
                    issue(
                        "architecture.unsupported.stage-loras-profile",
                        `Clip ${clipIdx} Stage ${stageIdx}${stage.skipped ? " (skipped)" : ""} has normal LoRAs, but model profile '${resolvedProfile.id}' does not support them.`,
                        clipIdx,
                    ),
                );
            }
            const samplingStageRule = resolvedProfile
                ? conditionalRule(
                      resolvedProfile.rules,
                      CONDITIONAL_RULE_CODES.normalLoraRequiresSamplingStage,
                  )
                : null;
            if (
                executableClipIndexSet.has(clipIdx) &&
                stageIdx < activeStageCount(clip) &&
                hasEffectiveNormalLora &&
                samplingStageRule &&
                evaluateConditionalRule(samplingStageRule, { clip, stage })
            ) {
                diagnostics.push(
                    issue(
                        samplingStageRule.code,
                        `Clip ${clipIdx} Stage ${stageIdx}: ${samplingStageRule.reason}`,
                        clipIdx,
                    ),
                );
            }
        });
    });

    for (const seam of executableBoundaries(clips)) {
        const left = { clip: clips[seam.leftIdx], clipIdx: seam.leftIdx };
        const right = { clip: clips[seam.rightIdx], clipIdx: seam.rightIdx };
        if (
            left.clip.architecture !== right.clip.architecture &&
            left.clip.boundaryOut !== "cut"
        ) {
            diagnostics.push(
                issue(
                    "architecture.cross-boundary-cut-only",
                    `Clip ${left.clipIdx} → ${right.clipIdx} crosses architectures; '${left.clip.boundaryOut}' is preserved for repair, but only cut can execute.`,
                    left.clipIdx,
                ),
            );
            continue;
        }
        const boundary = boundaries.forBoundary(
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
                    `Clip ${left.clipIdx} cannot execute a '${left.clip.boundaryOut}' boundary into Clip ${right.clipIdx}.${reason}`,
                    left.clipIdx,
                ),
            );
        }
    }
    return diagnostics;
};
