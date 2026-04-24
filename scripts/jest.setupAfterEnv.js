/**
 * Per-test scaffolding shared by every VideoStages frontend test.
 *
 * Runs after Jest's framework is loaded (setupFilesAfterEnv), so it can
 * register globalThis-level beforeEach/afterEach hooks. The goal is to
 * isolate tests from each other by resetting the small set of mutable
 * globals that VideoStages controllers read/write at runtime:
 *
 *   - postParamBuildSteps      SwarmUI's deferred init queue (params.js).
 *                              Controllers push their refresh callbacks
 *                              into it; if it's not an array, they fall
 *                              back to setTimeout polling, which leaks
 *                              timers across tests.
 *   - window.acestepfunTrackRegistry / window.base2editStageRegistry
 *                              Cross-extension snapshot bridges read by
 *                              audioSource and VideoStageEditorTemp.
 *   - document.body            Tests build their own DOM fixtures; we wipe
 *                              them between tests so nothing bleeds over.
 *
 * Things this file deliberately does NOT touch:
 *   - util.js / translator.js / site.js globals seeded by jest.setup.js.
 *     Those are loaded once per file and treated as read-only runtime.
 *   - Per-controller listeners on `document`. Tests that instantiate a
 *     controller are responsible for calling its `.dispose()` themselves
 *     (typically in a file-local afterEach), since the controller owns
 *     those listeners.
 */

beforeEach(() => {
    globalThis.postParamBuildSteps = [];
});

afterEach(() => {
    document.body.innerHTML = "";
    delete globalThis.postParamBuildSteps;
    delete window.acestepfunTrackRegistry;
    delete window.base2editStageRegistry;
});
