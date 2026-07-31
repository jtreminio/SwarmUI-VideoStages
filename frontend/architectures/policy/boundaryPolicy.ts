import {
    activeStageCount,
    executableBoundaries,
    executableBoundaryForLeftClip,
} from "../../clipSemantics";
import type { BoundaryOut, Clip } from "../../types";
import { boundaryOverlapConstraints } from "../boundaryConstraints";
import { resolvedClipArchitectureId } from "../clipIdentity";
import type {
    ArchitectureCatalogEntryDto,
    ArchitectureModelCatalog,
} from "../types";
import type { BoundaryCapabilityView, ClipCapabilityView } from "./types";

type ArchitectureLookup = ReadonlyMap<string, ArchitectureCatalogEntryDto>;

/**
 * Architecture conversion is the one authoring action that intentionally
 * rewrites a requested boundary. Other edits preserve the user's request and
 * rely on `BoundaryCapabilityView.effective()` for the executable cut.
 */
export const forceCrossArchitectureCutsForConversion = (
    clips: Clip[],
    catalog: ArchitectureModelCatalog | null,
): void => {
    for (const boundary of executableBoundaries(clips)) {
        const left = clips[boundary.leftIdx];
        const right = clips[boundary.rightIdx];
        const leftArchitectureId = resolvedClipArchitectureId(left, catalog);
        const rightArchitectureId = resolvedClipArchitectureId(right, catalog);
        if (
            leftArchitectureId !== null &&
            rightArchitectureId !== null &&
            leftArchitectureId !== rightArchitectureId
        ) {
            left.boundaryOut = "cut";
        }
    }
};

export const createBoundaryCapabilityViews = (
    architectureById: ArchitectureLookup,
    forClip: (clip: Clip) => ClipCapabilityView,
): {
    forBoundary(
        left: Clip,
        right: Clip | null,
        leftClipIdx?: number,
        rightClipIdx?: number | null,
    ): BoundaryCapabilityView;
    forBoundaryIndex(
        clips: readonly Clip[],
        leftClipIdx: number,
    ): BoundaryCapabilityView;
} => {
    const byTimeline = new WeakMap<
        readonly Clip[],
        Map<number, BoundaryCapabilityView>
    >();

    const forBoundary = (
        left: Clip,
        right: Clip | null,
        leftClipIdx = -1,
        rightClipIdx: number | null = null,
    ): BoundaryCapabilityView => {
        const leftView = forClip(left);
        const rightView = right === null ? null : forClip(right);
        const leftDescriptor = architectureById.get(leftView.architectureId);
        const crossArchitecture =
            rightView !== null &&
            leftView.architectureId !== rightView.architectureId;
        const hasInitialReference =
            right?.refs.some(
                (reference) =>
                    reference.fromEnd !== true &&
                    Math.max(1, Math.round(reference.frame)) === 1,
            ) ?? false;
        const rightHasActiveStage =
            right !== null && activeStageCount(right) > 0;
        const supportsMode = (mode: BoundaryOut): boolean => {
            const rule = leftDescriptor?.boundaryRules[mode];
            if (!rule || rule.support === "unsupported") {
                return mode === "cut" && !leftDescriptor;
            }
            const constraints = rule.constraints;
            if (constraints?.sameArchitecture === true && crossArchitecture) {
                return false;
            }
            if (
                constraints?.targetRequiresGeneratedEntry === true &&
                right?.sourceVideo !== null
            ) {
                return false;
            }
            if (
                constraints?.targetRequiresStage === true &&
                right !== null &&
                !rightHasActiveStage
            ) {
                return false;
            }
            if (
                constraints?.targetDisallowsInitialReference === true &&
                hasInitialReference
            ) {
                return false;
            }
            return true;
        };
        const ruleModes = (["cut", "continue", "crossfade"] as const).filter(
            supportsMode,
        );
        const modes: readonly BoundaryOut[] =
            ruleModes.length > 0 ? ruleModes : ["cut"];
        const requestedRule =
            leftDescriptor?.boundaryRules[left.boundaryOut] ?? null;
        const reason = crossArchitecture
            ? "Executable clips from different architectures can only use a cut."
            : modes.length === 1 && modes[0] === "cut"
              ? (requestedRule?.reason ??
                `${leftView.architectureLabel} only supports cut boundaries.`)
              : "";
        return {
            leftClipIdx,
            rightClipIdx,
            modes,
            crossArchitecture,
            reason,
            overlapConstraints: (mode) =>
                boundaryOverlapConstraints(
                    leftDescriptor?.boundaryRules[mode] ?? null,
                ),
            effective: (requested) =>
                supportsMode(requested) ? requested : "cut",
        };
    };

    const forBoundaryIndex = (
        clips: readonly Clip[],
        leftClipIdx: number,
    ): BoundaryCapabilityView => {
        let timelineViews = byTimeline.get(clips);
        if (!timelineViews) {
            timelineViews = new Map<number, BoundaryCapabilityView>();
            byTimeline.set(clips, timelineViews);
        }
        const cached = timelineViews.get(leftClipIdx);
        if (cached) {
            return cached;
        }
        const left = clips[leftClipIdx];
        if (!left) {
            throw new Error(`Missing left clip at index ${leftClipIdx}.`);
        }
        const rightClipIdx =
            executableBoundaryForLeftClip(clips, leftClipIdx)?.rightIdx ?? null;
        const view = forBoundary(
            left,
            rightClipIdx === null ? null : clips[rightClipIdx],
            leftClipIdx,
            rightClipIdx,
        );
        timelineViews.set(leftClipIdx, view);
        return view;
    };

    return {
        forBoundary,
        forBoundaryIndex,
    };
};
