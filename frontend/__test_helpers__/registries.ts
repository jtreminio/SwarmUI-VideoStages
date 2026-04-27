/**
 * Stubs for `window` snapshot registries (AceStepFun, Base2Edit) used by VideoStages tests.
 * Shared `setupFilesAfterEnv` clears them in `afterEach`.
 */

export const stubAceStepFunRegistry = (
    refs: string[] | null,
    enabled = true,
): void => {
    if (refs === null) {
        delete window.acestepfunTrackRegistry;
        return;
    }
    window.acestepfunTrackRegistry = {
        getSnapshot: () => ({
            enabled,
            trackCount: refs.length,
            refs,
        }),
    };
};

export const stubBase2EditStageRegistry = (
    refs: string[] | null,
    enabled = true,
    stageCount?: number,
): void => {
    if (refs === null) {
        delete window.base2editStageRegistry;
        return;
    }
    window.base2editStageRegistry = {
        getSnapshot: () => ({
            enabled,
            stageCount: stageCount ?? refs.length,
            refs,
        }),
    };
};

export const stubAceStepFunRegistryThrowing = (error: Error): void => {
    window.acestepfunTrackRegistry = {
        getSnapshot: () => {
            throw error;
        },
    };
};
