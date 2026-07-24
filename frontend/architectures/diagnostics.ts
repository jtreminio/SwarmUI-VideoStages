import { audioSourceKind, isAllowedAudioSource } from "../audioSource";
import { activeStageCount, isExecutableClip } from "../clipSemantics";
import type { Clip } from "../types";
import { isArchitectureHdrFeature } from "./behaviorRegistry";
import { architectureSupportsClipStart } from "./conversion/entryModePolicy";
import { upscaleModeForMethod } from "./policy";
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
    const clipCapabilities = capabilities.clip;
    const stageCapabilities = capabilities.stage;
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
        !capabilities.architecture.includes("multi-stage") &&
            activeStageCount(clip) > 1,
        "multi-stage",
        "Multiple active stages",
    );
    unsupported(
        (!clipCapabilities.includes("references") ||
            !stageCapabilities.includes("frame-references")) &&
            clip.refs.length > 0,
        "frame-references",
        "Frame references",
    );
    unsupported(
        !stageCapabilities.includes("ic-lora") && clip.icLoras.length > 0,
        "ic-lora",
        "IC-LoRA",
    );
    unsupported(
        !stageCapabilities.includes("hdr") &&
            clip.icLoras.some((entry) =>
                isArchitectureHdrFeature(clip.architecture, entry),
            ),
        "hdr",
        "HDR",
    );
    unsupported(
        !clipCapabilities.includes("retake") && clip.retake !== null,
        "retake",
        "Retake",
    );
    unsupported(
        !clipCapabilities.includes("prompts") && clip.prompt.trim().length > 0,
        "major-prompt",
        "Major prompt",
    );
    unsupported(
        !clipCapabilities.includes("source-video") && clip.sourceVideo !== null,
        "source-video",
        "Source video",
    );
    unsupported(
        !clipCapabilities.includes("prompt-relay") &&
            clip.promptWindows.length > 0,
        "prompt-relay",
        "Prompt relay",
    );
    unsupported(
        !stageCapabilities.includes("lora") &&
            clip.stages.some((stage) => stage.loras.length > 0),
        "stage-loras",
        "Stage LoRAs",
    );
    unsupported(
        clip.stages.some(
            (stage) =>
                stage.upscale !== 1 &&
                !capabilities.upscaleModes.includes(
                    upscaleModeForMethod(stage.upscaleMethod),
                ),
        ),
        "upscale",
        "Stage upscaling",
    );
    const sourceKind = audioSourceKind(clip.audioSource);
    unsupported(
        (!clipCapabilities.includes("audio-sources") ||
            !isAllowedAudioSource(
                capabilities.audioSourceKinds,
                clip.audioSource,
            )) &&
            (sourceKind !== "Native" ||
                clip.uploadedAudio !== null ||
                clip.saveAudioTrack ||
                clip.reuseAudio ||
                clip.clipLengthFromAudio ||
                clip.clipLengthFromControlNet),
        "audio-source",
        `Audio source '${sourceKind}'`,
    );
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
    const modelByName = new Map(
        catalog.entries.map((entry) => [entry.value, entry]),
    );

    clips.forEach((clip, clipIdx) => {
        const sourceOnly =
            activeStageCount(clip) === 0 && clip.sourceVideo !== null;
        if (sourceOnly) {
            if (
                clip.architecture !== "none" ||
                clip.modelProfileId !== "none"
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
                !architectureSupportsClipStart(
                    architecture.capabilities,
                    clip,
                    generatedEntryMode,
                )
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
            if (
                stage.loras.length > 0 &&
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
        });
    });

    const executable = clips
        .map((clip, clipIdx) => ({ clip, clipIdx }))
        .filter(({ clip }) => isExecutableClip(clip));
    for (let index = 0; index < executable.length - 1; index++) {
        const left = executable[index];
        const right = executable[index + 1];
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
        const descriptor = architectureById.get(left.clip.architecture);
        if (
            descriptor &&
            descriptor.boundaryRules[left.clip.boundaryOut]?.support ===
                "unsupported"
        ) {
            diagnostics.push(
                issue(
                    "architecture.boundary-unsupported",
                    `Clip ${left.clipIdx} architecture '${left.clip.architecture}' does not support '${left.clip.boundaryOut}' boundaries.`,
                    left.clipIdx,
                ),
            );
        }
    }
    return diagnostics;
};
