import type { Clip } from "../../types";
/** Pure entry-mode gate shared by diagnostics and model filtering. */
export const architectureSupportsClipStart = (
    entryModes: readonly string[],
    clip: Clip,
    generatedEntryMode: "text-to-video" | "image-to-video",
): boolean => {
    const modes = entryModes;
    if (clip.sourceVideo !== null) {
        return modes.includes("source-video");
    }
    // Global refine is a one-shot host override, not a persisted clip authoring
    // mode. Frame references guide the chosen host mode; they do not replace it.
    return modes.includes(generatedEntryMode);
};
