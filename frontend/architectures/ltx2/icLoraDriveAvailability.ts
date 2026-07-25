import { isExecutableClip } from "../../clipSemantics";
import {
    IC_LORA_SOURCE_INCOMING,
    IC_LORA_SOURCE_UPLOAD,
} from "../../icLoraAuthoring";
import type { Clip, IcLora } from "../../types";

export type GeneratedEntryMode = "text-to-video" | "image-to-video";

/** Whether every stage targeted by an entry has compatible media entering it. */
export const canUseIncomingIcLoraDrive = (
    entry: IcLora,
    clip: Clip,
    clipIdx: number,
    clips: readonly Clip[],
    generatedEntryMode: GeneratedEntryMode,
): boolean => {
    if (entry.driveData === "none" || !isExecutableClip(clip)) {
        return false;
    }
    const acceptedKinds = entry.driveMediaKinds;
    const activeStageIndexes = clip.stages.flatMap((stage, rawIndex) =>
        stage.skipped ? [] : [rawIndex],
    );
    const targetedStages =
        entry.stage >= 0
            ? activeStageIndexes.includes(entry.stage)
                ? [entry.stage]
                : []
            : activeStageIndexes;
    const hasPreviousClipOutput = clips
        .slice(0, clipIdx)
        .some(isExecutableClip);
    return (
        targetedStages.length > 0 &&
        targetedStages.every((targetStage) => {
            const activeStageIndex = activeStageIndexes.indexOf(targetStage);
            const incomingKind =
                activeStageIndex > 0 || clip.sourceVideo
                    ? "video"
                    : hasPreviousClipOutput
                      ? "video"
                      : generatedEntryMode === "image-to-video"
                        ? "image"
                        : null;
            return (
                incomingKind !== null && acceptedKinds.includes(incomingKind)
            );
        })
    );
};

/**
 * Incoming is a live reference to the execution graph, not a persisted media
 * object. Repair entries as soon as a skip toggle removes that graph input.
 */
export const reconcileIncomingIcLoraDrives = (
    clips: Clip[],
    clipIdx: number,
    generatedEntryMode: GeneratedEntryMode,
): boolean => {
    const clip = clips[clipIdx];
    if (!clip) {
        return false;
    }
    let changed = false;
    for (const entry of clip.icLoras) {
        if (
            entry.driveSource === IC_LORA_SOURCE_INCOMING &&
            !canUseIncomingIcLoraDrive(
                entry,
                clip,
                clipIdx,
                clips,
                generatedEntryMode,
            )
        ) {
            entry.driveSource = IC_LORA_SOURCE_UPLOAD;
            changed = true;
        }
    }
    return changed;
};
