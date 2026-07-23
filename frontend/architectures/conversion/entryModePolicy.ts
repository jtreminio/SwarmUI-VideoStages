import type { Clip } from "../../types";
import type { ArchitectureRetargetPlan } from "../types";

/** Pure entry-mode gate shared by diagnostics and model filtering. */
export const architectureSupportsClipStart = (
    capabilities: ArchitectureRetargetPlan["capabilities"],
    clip: Clip,
    generatedEntryMode: "text-to-video" | "image-to-video",
): boolean => {
    const modes = capabilities.entryModes;
    if (clip.sourceVideo !== null) {
        return modes.includes("source-video") || modes.includes("refine-video");
    }
    const hasInitialReference = clip.refs.some(
        (reference) =>
            reference.fromEnd !== true &&
            Math.max(1, Math.round(reference.frame)) === 1,
    );
    return hasInitialReference
        ? modes.includes("image-to-video")
        : modes.includes(generatedEntryMode);
};
