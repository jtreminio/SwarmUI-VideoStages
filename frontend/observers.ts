import { videoStagesDebugLog } from "./debugLog";
import { type SaveStateOptions, serializeStateForStorage } from "./persistence";
import { getRootDefaults } from "./rootDefaults";
import { getClipsInput } from "./swarmInputs";
import type { VideoStagesConfig } from "./types";
import { utils } from "./utils";

const ROOT_VIDEO_TIMING_INPUT_IDS = new Set<string>([
    "input_videoframes",
    "input_text2videoframes",
    "input_videofps",
    "input_videoframespersecond",
    "input_videostagesfps",
]);

export type ObserversApi = {
    markPersisted: (serialized: string) => void;
    startClipsInputSync: () => void;
    installSourceDropdownObserver: () => void;
    installBase2EditStageChangeListener: () => void;
    installRootVideoTimingChangeListener: () => void;
    installRefSourceFallbackListener: (
        createEditor: () => void,
        handleFieldChange: (
            target: EventTarget | null,
            sourceEvent?: Event,
        ) => void,
    ) => void;
};

export const createObservers = (deps: {
    scheduleRefresh: () => void;
    getState: () => VideoStagesConfig;
    saveState: (state: VideoStagesConfig, options?: SaveStateOptions) => void;
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
            videoStagesDebugLog(
                "observers",
                "clips input JSON drift → scheduleRefresh",
                {
                    prevChars: lastKnownClipsJson.length,
                    nextChars: currentValue.length,
                },
            );
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
            const first = mutations.find((m) => m.type === "childList");
            const targetId =
                first?.target instanceof HTMLElement
                    ? first.target.id || "(no id)"
                    : null;
            videoStagesDebugLog(
                "observers",
                "source dropdown childList mutation → scheduleRefresh",
                { targetId, mutationCount: mutations.length },
            );
            deps.scheduleRefresh();
        });

        const observableIds = [
            "input_videomodel",
            "input_model",
            "input_vae",
            "input_sampler",
            "input_scheduler",
            "input_refinerupscalemethod",
            "input_loras",
        ];

        let hasObservedSource = false;
        for (const sourceId of observableIds) {
            const source = utils.getSelectElement(sourceId);
            if (!source || observedDropdownIds.has(sourceId)) {
                continue;
            }
            observedDropdownIds.add(sourceId);
            observer.observe(source, { childList: true });
            source.addEventListener("change", () => {
                videoStagesDebugLog(
                    "observers",
                    "observed source select change → scheduleRefresh",
                    { sourceId },
                );
                deps.scheduleRefresh();
            });
            hasObservedSource = true;
        }

        if (!hasObservedSource) {
            observer.disconnect();
            return;
        }

        sourceDropdownObserver = observer;
    };

    const handleRootVideoTimingCommittedChange = (inputId: string): void => {
        const input = getClipsInput();
        if (!input) {
            return;
        }

        const state = deps.getState();
        const rootDefaults = getRootDefaults();
        state.width = rootDefaults.width;
        state.height = rootDefaults.height;
        state.fps = rootDefaults.fps;
        const serialized = serializeStateForStorage(state);
        if (serialized !== input.value) {
            videoStagesDebugLog(
                "observers",
                "root video timing change → saveState (notifyDomChange: false)",
                { inputId },
            );
            deps.saveState(state, { notifyDomChange: false });
        }
        videoStagesDebugLog(
            "observers",
            "root video timing change → scheduleRefresh",
            { inputId },
        );
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

            handleRootVideoTimingCommittedChange(target.id);
        });
    };

    const installBase2EditStageChangeListener = (): void => {
        if (base2EditListenerInstalled) {
            return;
        }
        base2EditListenerInstalled = true;
        document.addEventListener("base2edit:stages-changed", () => {
            videoStagesDebugLog(
                "observers",
                "base2edit:stages-changed → scheduleRefresh",
            );
            deps.scheduleRefresh();
        });
    };

    const installRefSourceFallbackListener = (
        createEditor: () => void,
        handleFieldChange: (
            target: EventTarget | null,
            sourceEvent?: Event,
        ) => void,
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
                const clipField = target.dataset.clipField;
                const isClipAudioSourceChange = clipField === "audioSource";
                const isControlNetSourceChange =
                    clipField === "controlNetSource";
                const isControlNetLoraChange = clipField === "controlNetLora";
                if (
                    !isRefSourceChange &&
                    !isClipAudioSourceChange &&
                    !isControlNetSourceChange &&
                    !isControlNetLoraChange
                ) {
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

                videoStagesDebugLog(
                    "observers",
                    "ref-source fallback capture change → createEditor + handleFieldChange",
                    {
                        refField: target.dataset.refField ?? null,
                        clipField: clipField ?? null,
                        selectId: target.id || null,
                    },
                );
                createEditor();
                handleFieldChange(target, event);
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
