import type { Clip } from "../../types";
import type { ArchitectureModelEntry } from "../types";

export type GeneratedEntryMode = "text-to-video" | "image-to-video";

const firstActiveStageIndex = (clip: Clip): number | null => {
    const index = clip.stages.findIndex((stage) => !stage.skipped);
    return index >= 0 ? index : null;
};

/**
 * Whether a host model can enter the timeline at this stage's role.
 *
 * The first active stage must accept the clip's root input. Every later active
 * stage receives decoded images from the previous stage, regardless of the
 * host input that began the clip.
 */
export const modelSupportsStageEntry = (
    model: Pick<ArchitectureModelEntry, "entryModes"> &
        Partial<Pick<ArchitectureModelEntry, "entryAbilities">>,
    clip: Clip,
    stageIdx: number,
    generatedEntryMode: GeneratedEntryMode,
): boolean => {
    const supportsText =
        model.entryAbilities?.includes("text") ??
        model.entryModes.includes("text-to-video");
    const supportsImage =
        model.entryAbilities?.includes("image") ??
        model.entryModes.includes("image-to-video");
    const firstActive = firstActiveStageIndex(clip);
    const isClipRoot =
        firstActive === null
            ? !clip.stages
                  .slice(0, stageIdx)
                  .some((candidate) => !candidate.skipped)
            : stageIdx === firstActive;
    if (!isClipRoot) {
        return supportsImage;
    }
    if (clip.sourceVideo === null) {
        // Frame references guide this root mode; they do not replace it.
        return generatedEntryMode === "text-to-video"
            ? supportsText
            : supportsImage;
    }
    return supportsImage || model.entryModes.includes("source-video");
};

/** Whether one model can perform every active role after a whole-clip retarget. */
export const modelSupportsAllActiveStageEntries = (
    model: Pick<ArchitectureModelEntry, "entryModes"> &
        Partial<Pick<ArchitectureModelEntry, "entryAbilities">>,
    clip: Clip,
    generatedEntryMode: GeneratedEntryMode,
): boolean =>
    clip.stages.every(
        (stage, stageIdx) =>
            stage.skipped ||
            modelSupportsStageEntry(model, clip, stageIdx, generatedEntryMode),
    );
