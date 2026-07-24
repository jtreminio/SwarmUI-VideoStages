import {
    defaultAuthoringAudioSource,
    isAllowedAudioSource,
} from "../../audioSource";
import {
    IC_LORA_SOURCE_UPLOAD,
    IC_LORA_STAGE_ALL,
} from "../../icLoraAuthoring";
import type { Clip } from "../../types";
import { isArchitectureHdrFeature } from "../behaviorRegistry";
import { upscaleModeForMethod } from "../policy";
import type {
    ArchitectureModelCatalog,
    ArchitectureRetargetPlan,
} from "../types";

const countLabel = (count: number, singular: string): string =>
    `${count} ${singular}${count === 1 ? "" : "s"}`;

export interface ArchitectureConversionPlan {
    /** The complete converted clip; the source clip is never mutated. */
    clip: Clip;
    /** User-facing summary produced from the same decisions as `clip`. */
    removals: string[];
    /** Stable IDs removed by the conversion, for selection invalidation. */
    removedEntityIds: string[];
    selectionAffected: boolean;
}

interface ResolvedArchitectureRetarget extends ArchitectureRetargetPlan {
    profileCapabilities: string[];
}

const ownId = (value: unknown): string | null =>
    typeof value === "object" &&
    value !== null &&
    "id" in value &&
    typeof value.id === "string"
        ? value.id
        : null;

const collectIds = (values: readonly unknown[]): string[] =>
    values.map(ownId).filter((id): id is string => id !== null);

/**
 * Resolves a caller-supplied target against the catalog. Every supplied
 * identity must match; caller-supplied capability arrays never self-authorize.
 */
export const resolveArchitectureRetarget = (
    requested: Pick<
        ArchitectureRetargetPlan,
        "architectureId" | "modelProfileId" | "model"
    >,
    catalog: ArchitectureModelCatalog | null,
): ResolvedArchitectureRetarget | null => {
    if (!catalog) {
        return null;
    }
    const model = catalog.entries.find(
        (entry) => entry.value === requested.model,
    );
    if (
        !model?.architectureId ||
        !model.modelProfileId ||
        model.architectureId !== requested.architectureId ||
        model.modelProfileId !== requested.modelProfileId
    ) {
        return null;
    }
    const descriptor = catalog.architectures.find(
        (entry) => entry.id === model.architectureId,
    );
    const profile = descriptor?.profiles.find(
        (entry) => entry.id === model.modelProfileId,
    );
    if (!descriptor || !profile) {
        return null;
    }
    return {
        architectureId: descriptor.id,
        modelProfileId: profile.id,
        model: model.value,
        capabilities: structuredClone(descriptor.capabilities),
        profileCapabilities: [...profile.capabilities],
    };
};

/**
 * Plans and applies one architecture conversion on a private clone.
 *
 * Preview and reducer both consume this function, so the confirmation summary
 * cannot drift from the actual atomic mutation.
 */
export const planArchitectureConversion = (
    source: Clip,
    requested: ArchitectureRetargetPlan,
    catalog: ArchitectureModelCatalog | null,
): ArchitectureConversionPlan | null => {
    const target = resolveArchitectureRetarget(requested, catalog);
    if (!target) {
        return null;
    }

    const clip = structuredClone(source);
    const removals: string[] = [];
    const removedEntityIds: string[] = [];
    const clipCapabilities = target.capabilities.clip;
    const stageCapabilities = target.capabilities.stage;
    const supportsMultipleStages =
        target.capabilities.architecture.includes("multi-stage");
    const supportsReferences =
        clipCapabilities.includes("references") &&
        stageCapabilities.includes("frame-references");
    const supportsNormalLoras =
        stageCapabilities.includes("lora") &&
        target.profileCapabilities.includes("normal-lora");

    clip.architecture = target.architectureId;
    clip.modelProfileId = target.modelProfileId;

    if (!supportsMultipleStages && clip.stages.length > 1) {
        const removedStages = clip.stages.slice(1);
        removals.push(countLabel(removedStages.length, "later authored stage"));
        removedEntityIds.push(...collectIds(removedStages));
        clip.stages = clip.stages.slice(0, 1);
    }

    if (!supportsReferences && clip.refs.length > 0) {
        removals.push(countLabel(clip.refs.length, "frame reference"));
        removedEntityIds.push(...collectIds(clip.refs));
        clip.refs = [];
    }

    let removedStageLoras = 0;
    let removedUpscaleSettings = 0;
    for (const stage of clip.stages) {
        stage.model = target.model;
        stage.modelProfileId = target.modelProfileId;
        if (!supportsReferences) {
            stage.refStrengths = [];
            stage.icLoraStrengths = [];
        }
        if (!supportsNormalLoras && stage.loras.length > 0) {
            removedStageLoras += stage.loras.length;
            stage.loras = [];
        }
        if (
            stage.upscale !== 1 &&
            !target.capabilities.upscaleModes.includes(
                upscaleModeForMethod(stage.upscaleMethod),
            )
        ) {
            removedUpscaleSettings++;
            stage.upscale = 1;
        }
    }
    if (removedStageLoras > 0) {
        removals.push(countLabel(removedStageLoras, "stage LoRA"));
    }
    if (removedUpscaleSettings > 0) {
        removals.push("stage upscale settings");
    }

    if (!stageCapabilities.includes("ic-lora") && clip.icLoras.length > 0) {
        removals.push(countLabel(clip.icLoras.length, "IC-LoRA"));
        clip.icLoras = [];
        clip.clipLengthFromControlNet = false;
    } else if (stageCapabilities.includes("ic-lora")) {
        if (!stageCapabilities.includes("hdr")) {
            const hdrCount = clip.icLoras.filter((entry) =>
                isArchitectureHdrFeature(source.architecture, entry),
            ).length;
            if (hdrCount > 0) {
                removals.push(countLabel(hdrCount, "HDR IC-LoRA"));
                clip.icLoras = clip.icLoras.filter(
                    (entry) =>
                        !isArchitectureHdrFeature(source.architecture, entry),
                );
            }
        }
        let repairedTargets = false;
        for (const entry of clip.icLoras) {
            if (entry.stage >= clip.stages.length) {
                entry.stage = IC_LORA_STAGE_ALL;
                repairedTargets = true;
            }
            if (
                entry.driveData === "none" &&
                entry.driveSource !== IC_LORA_SOURCE_UPLOAD
            ) {
                entry.driveSource = IC_LORA_SOURCE_UPLOAD;
                entry.driveMedia = null;
            }
        }
        if (repairedTargets) {
            removals.push("IC-LoRA targets on removed stages");
        }
    }

    if (!clipCapabilities.includes("retake") && clip.retake !== null) {
        removals.push("retake");
        const id = ownId(clip.retake);
        if (id) removedEntityIds.push(id);
        clip.retake = null;
    }
    if (!clipCapabilities.includes("prompts") && clip.prompt.trim()) {
        removals.push("major prompt");
        clip.prompt = "";
    }
    if (
        !clipCapabilities.includes("prompt-relay") &&
        clip.promptWindows.length > 0
    ) {
        removals.push(countLabel(clip.promptWindows.length, "relay prompt"));
        removedEntityIds.push(...collectIds(clip.promptWindows));
        clip.promptWindows = [];
    }
    if (
        !clipCapabilities.includes("audio-segments") &&
        clip.audioSegments.length > 0
    ) {
        removals.push(countLabel(clip.audioSegments.length, "audio segment"));
        removedEntityIds.push(...collectIds(clip.audioSegments));
        clip.audioSegments = [];
    }
    if (!clipCapabilities.includes("source-video") && clip.sourceVideo) {
        removals.push("source video");
        clip.sourceVideo = null;
    }
    if (
        !clipCapabilities.includes("audio-sources") ||
        !isAllowedAudioSource(
            target.capabilities.audioSourceKinds,
            clip.audioSource,
        )
    ) {
        const hasAudioSettings =
            clip.audioSource !== "Native" ||
            clip.uploadedAudio !== null ||
            clip.saveAudioTrack ||
            clip.clipLengthFromAudio ||
            clip.reuseAudio;
        if (hasAudioSettings) {
            removals.push("clip audio source settings");
        }
        clip.audioSource = defaultAuthoringAudioSource(
            target.capabilities.audioSourceKinds,
        );
        clip.uploadedAudio = null;
        clip.saveAudioTrack = false;
        clip.clipLengthFromAudio = false;
        clip.reuseAudio = false;
    }

    return {
        clip,
        removals,
        removedEntityIds,
        selectionAffected: removedEntityIds.length > 0,
    };
};
