import { CONTINUE_MODES } from "./generatedFeatures";
import type { CapabilityRuleDecision } from "./types";

type ContinueMode = (typeof CONTINUE_MODES)[number];

export interface BoundaryWindowConstraints {
    frameStep: number;
    minFrames: number;
    maxFrames: number;
    defaultFrames: number;
    continuityExtraFrames: number;
    continueMode: ContinueMode;
}

const GENERIC_BOUNDARY_CONSTRAINTS: BoundaryWindowConstraints = {
    frameStep: 1,
    minFrames: 1,
    maxFrames: Number.MAX_SAFE_INTEGER,
    defaultFrames: 1,
    continuityExtraFrames: 0,
    continueMode: "overlap",
};

const asContinueMode = (value: unknown): ContinueMode =>
    CONTINUE_MODES.find((mode) => mode === value) ??
    GENERIC_BOUNDARY_CONSTRAINTS.continueMode;

const finitePositive = (
    value: unknown,
    fallback: number,
    allowZero = false,
): number => {
    const numeric = Math.trunc(Number(value));
    return Number.isFinite(numeric) && (allowZero ? numeric >= 0 : numeric > 0)
        ? numeric
        : fallback;
};

export const boundaryWindowConstraints = (
    rule: CapabilityRuleDecision | null | undefined,
): BoundaryWindowConstraints => {
    const raw = rule?.constraints;
    const frameStep = finitePositive(
        raw?.frameStep,
        GENERIC_BOUNDARY_CONSTRAINTS.frameStep,
    );
    const minFrames = finitePositive(
        raw?.minFrames,
        GENERIC_BOUNDARY_CONSTRAINTS.minFrames,
    );
    const maxFrames = Math.max(
        minFrames,
        finitePositive(raw?.maxFrames, GENERIC_BOUNDARY_CONSTRAINTS.maxFrames),
    );
    const authoredDefault = finitePositive(raw?.defaultFrames, minFrames);
    return {
        frameStep,
        minFrames,
        maxFrames,
        defaultFrames: Math.max(
            minFrames,
            Math.min(maxFrames, authoredDefault),
        ),
        continuityExtraFrames: finitePositive(
            raw?.continuityExtraFrames,
            GENERIC_BOUNDARY_CONSTRAINTS.continuityExtraFrames,
            true,
        ),
        continueMode: asContinueMode(raw?.continueMode),
    };
};

export const normalizeBoundaryWindow = (
    value: unknown,
    constraints: BoundaryWindowConstraints,
): number => {
    const numeric = Math.trunc(Number(value));
    if (!Number.isFinite(numeric) || numeric <= 0) {
        return constraints.defaultFrames;
    }
    const snapped =
        numeric < constraints.minFrames
            ? constraints.minFrames
            : constraints.minFrames +
              Math.floor(
                  (numeric - constraints.minFrames) / constraints.frameStep,
              ) *
                  constraints.frameStep;
    return Math.max(
        constraints.minFrames,
        Math.min(constraints.maxFrames, snapped),
    );
};

export const boundaryWindowChoices = (
    constraints: BoundaryWindowConstraints,
): number[] => {
    const choices: number[] = [];
    for (
        let value = constraints.minFrames;
        value <= constraints.maxFrames;
        value += constraints.frameStep
    ) {
        choices.push(value);
        if (choices.length >= 100) break;
    }
    return choices;
};
