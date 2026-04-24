import { serializeClipsForStorage } from "./persistence";
import { getClipsInput } from "./swarmInputs";
import type { VideoStagesConfig } from "./Types";
import { VideoStageUtils } from "./Utils";

const ROOT_VIDEO_TIMING_INPUT_IDS = new Set<string>([
    "input_videoframes",
    "input_text2videoframes",
    "input_videofps",
    "input_videoframespersecond",
    "input_vsfps",
]);

export type ObserversApi = {
    markPersisted: (serialized: string) => void;
    startClipsInputSync: () => void;
    installSourceDropdownObserver: () => void;
    installBase2EditStageChangeListener: () => void;
    installRootVideoTimingChangeListener: () => void;
    installRefSourceFallbackListener: (
        createEditor: () => void,
        handleFieldChange: (target: EventTarget | null) => void,
    ) => void;
};

export const createObservers = (deps: {
    scheduleRefresh: () => void;
    getState: () => VideoStagesConfig;
    saveState: (state: VideoStagesConfig) => void;
}): ObserversApi => {
    let clipsInputSyncInterval: ReturnType<typeof setInterval> | null = null;
    let lastKnownClipsJson = "";
    const observedDropdownIds = new Set<string>();
    let sourceDropdownObserver: MutationObserver | null = null;
    let base2EditListenerInstalled = false;
    let rootVideoTimingChangeListenerInstalled = false;
    let refSourceFallbackListenerInstalled = false;

    const markPersisted = (serialized: string): void => {
        lastKnownClipsJson = serialized;
    };

    const startClipsInputSync = (): void => {
        if (clipsInputSyncInterval) {
            return;
        }

        lastKnownClipsJson = getClipsInput()?.value ?? "";
        clipsInputSyncInterval = setInterval(() => {
            const currentValue = getClipsInput()?.value ?? "";
            if (currentValue === lastKnownClipsJson) {
                return;
            }
            lastKnownClipsJson = currentValue;
            deps.scheduleRefresh();
        }, 150);
    };

    const installSourceDropdownObserver = (): void => {
        if (sourceDropdownObserver || typeof MutationObserver === "undefined") {
            return;
        }

        const observer = new MutationObserver((mutations) => {
            if (!mutations.some((mutation) => mutation.type === "childList")) {
                return;
            }
            deps.scheduleRefresh();
        });

        const observableIds = [
            "input_videomodel",
            "input_model",
            "input_vae",
            "input_sampler",
            "input_scheduler",
            "input_refinerupscalemethod",
        ];

        let hasObservedSource = false;
        for (const sourceId of observableIds) {
            const source = VideoStageUtils.getSelectElement(sourceId);
            if (!source || observedDropdownIds.has(sourceId)) {
                continue;
            }
            observedDropdownIds.add(sourceId);
            observer.observe(source, { childList: true });
            source.addEventListener("change", () => deps.scheduleRefresh());
            hasObservedSource = true;
        }

        if (!hasObservedSource) {
            observer.disconnect();
            return;
        }

        sourceDropdownObserver = observer;
    };

    const handleRootVideoTimingCommittedChange = (): void => {
        const input = getClipsInput();
        if (!input) {
            return;
        }

        const state = deps.getState();
        const serialized = JSON.stringify({
            width: state.width,
            height: state.height,
            fps: state.fps,
            clips: serializeClipsForStorage(state.clips),
        });
        if (serialized !== input.value) {
            deps.saveState(state);
        }
        deps.scheduleRefresh();
    };

    const installRootVideoTimingChangeListener = (): void => {
        if (rootVideoTimingChangeListenerInstalled) {
            return;
        }
        rootVideoTimingChangeListenerInstalled = true;
        document.addEventListener("change", (event) => {
            if (!(event.target instanceof HTMLInputElement)) {
                return;
            }
            const target = event.target;
            if (!ROOT_VIDEO_TIMING_INPUT_IDS.has(target.id)) {
                return;
            }

            handleRootVideoTimingCommittedChange();
        });
    };

    const installBase2EditStageChangeListener = (): void => {
        if (base2EditListenerInstalled) {
            return;
        }
        base2EditListenerInstalled = true;
        document.addEventListener("base2edit:stages-changed", () => {
            deps.scheduleRefresh();
        });
    };

    const installRefSourceFallbackListener = (
        createEditor: () => void,
        handleFieldChange: (target: EventTarget | null) => void,
    ): void => {
        if (refSourceFallbackListenerInstalled) {
            return;
        }
        refSourceFallbackListenerInstalled = true;
        document.addEventListener(
            "change",
            (event) => {
                if (!(event.target instanceof HTMLSelectElement)) {
                    return;
                }
                const target = event.target;
                const isRefSourceChange = target.dataset.refField === "source";
                const isClipAudioSourceChange =
                    target.dataset.clipField === "audioSource";
                if (!isRefSourceChange && !isClipAudioSourceChange) {
                    return;
                }
                const liveEditor = document.getElementById(
                    "videostages_stage_editor",
                );
                if (!(liveEditor instanceof HTMLElement)) {
                    return;
                }
                if (!liveEditor.contains(target)) {
                    return;
                }

                createEditor();
                handleFieldChange(target);
            },
            true,
        );
    };

    return {
        markPersisted,
        startClipsInputSync,
        installSourceDropdownObserver,
        installBase2EditStageChangeListener,
        installRootVideoTimingChangeListener,
        installRefSourceFallbackListener,
    };
};
