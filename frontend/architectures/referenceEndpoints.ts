import { activeStageCount } from "../clipSemantics";
import type { Clip } from "../types";
import { modelCatalogEntry } from "./catalog";
import type { ArchitectureModelCatalog } from "./types";

export interface ClipReferenceEndpointPolicy {
    positions: string[];
    bounded: boolean;
    supportsFirst: boolean;
    supportsLast: boolean;
}

/**
 * Resolves bounded reference ownership from the stages that actually consume
 * each endpoint. The first effective model owns the first image; the terminal
 * generating model owns the final image.
 */
export const referenceEndpointPolicy = (
    clip: Clip,
    catalog: ArchitectureModelCatalog,
): ClipReferenceEndpointPolicy => {
    const activeStages = clip.stages.slice(0, activeStageCount(clip));
    const firstPositions =
        modelCatalogEntry(catalog, activeStages[0]?.model)?.enhancements
            ?.referencePositions ?? [];
    let terminalGenerating = activeStages.at(-1);
    while (terminalGenerating && terminalGenerating.control <= 0) {
        activeStages.pop();
        terminalGenerating = activeStages.at(-1);
    }
    const lastPositions =
        modelCatalogEntry(catalog, terminalGenerating?.model)?.enhancements
            ?.referencePositions ?? [];
    if (firstPositions.includes("any")) {
        return {
            positions: ["any"],
            bounded: false,
            supportsFirst: true,
            supportsLast: true,
        };
    }
    const supportsFirst = firstPositions.includes("first");
    const supportsLast = lastPositions.includes("last");
    const positions = [
        ...(supportsFirst ? ["first"] : []),
        ...(supportsLast ? ["last"] : []),
    ];
    return {
        positions,
        bounded: positions.length > 0,
        supportsFirst,
        supportsLast,
    };
};

export const boundedReferencePositionHelp = (
    policy: ClipReferenceEndpointPolicy,
): string | undefined => {
    if (!policy.bounded) {
        return undefined;
    }
    if (policy.supportsFirst && policy.supportsLast) {
        return "This clip accepts an image only at the first or final frame.";
    }
    if (policy.supportsFirst) {
        return "This clip accepts an image only at the first frame.";
    }
    return "This clip accepts an image only at the final frame.";
};

export const boundedReferenceToggleHelp = (
    policy: ClipReferenceEndpointPolicy,
): string | undefined => {
    if (!policy.bounded) {
        return undefined;
    }
    if (policy.supportsFirst && policy.supportsLast) {
        return "Off means first frame; on means final frame.";
    }
    return policy.supportsFirst
        ? "This clip supports only the first frame. Turn this off to repair an older final-frame value."
        : "This clip supports only the final frame. Turn this on to repair an older first-frame value.";
};
