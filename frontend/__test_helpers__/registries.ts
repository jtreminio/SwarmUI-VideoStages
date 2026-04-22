/**
 * Stubs for the cross-extension snapshot registries that VideoStages
 * frontend code reads from `window`.
 *
 * Each stub installs a minimal `getSnapshot` implementation that returns the
 * given refs. Pass `null` (or call `clear*Registry`) to remove the stub. The
 * shared `setupFilesAfterEnv` scaffolding always deletes both registries in
 * its `afterEach`, so callers do not need to clear them manually unless the
 * test re-stubs midway through.
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

/**
 * Installs an AceStepFun registry whose `getSnapshot` raises. Useful for
 * exercising error-handling paths in callers like `runOnEachBuild`.
 */
export const stubAceStepFunRegistryThrowing = (error: Error): void => {
    window.acestepfunTrackRegistry = {
        getSnapshot: () => {
            throw error;
        },
    };
};
