import type {
    HostOptionList,
    HostRegistrySnapshot,
    PromptPrefixExamples,
    VideoStagesHostBridge,
} from "./VideoStagesHostBridge";

const textInput = (
    id: string,
): HTMLInputElement | HTMLTextAreaElement | null => {
    const element = document.getElementById(id);
    return element instanceof HTMLInputElement ||
        element instanceof HTMLTextAreaElement
        ? element
        : null;
};

const input = (id: string): HTMLInputElement | null => {
    const element = document.getElementById(id);
    return element instanceof HTMLInputElement ? element : null;
};

const select = (id: string): HTMLSelectElement | null => {
    const element = document.getElementById(id);
    return element instanceof HTMLSelectElement ? element : null;
};

const selectOptions = (element: HTMLSelectElement | null): HostOptionList => ({
    values: element
        ? Array.from(element.options, (option) => option.value)
        : [],
    labels: element
        ? Array.from(element.options, (option) => option.label)
        : [],
});

const registrySnapshot = (
    registry: { getSnapshot?: () => HostRegistrySnapshot } | null | undefined,
): HostRegistrySnapshot | null => registry?.getSnapshot?.() ?? null;

const withSuppressedPromptCompletion = (fn: () => void): void => {
    const completion =
        typeof promptTabComplete !== "undefined" ? promptTabComplete : null;
    if (!completion) {
        fn();
        return;
    }
    const previous = completion.blockInput;
    completion.blockInput = true;
    try {
        fn();
    } finally {
        completion.blockInput = previous;
    }
};

export const createDefaultVideoStagesHostBridge =
    (): VideoStagesHostBridge => ({
        hasElement: (id) => document.getElementById(id) !== null,
        getTextInput: textInput,
        getInput: input,
        getRootVideoFpsInput: () =>
            input("input_videofps") ?? input("input_videoframespersecond"),
        getSelect: select,
        getSelectOptions: selectOptions,
        getParamOptions: (paramId) => {
            if (typeof getParamById !== "function") {
                return null;
            }
            const param = getParamById(paramId);
            if (!Array.isArray(param?.values) || param.values.length === 0) {
                return null;
            }
            const labels =
                Array.isArray(param.value_names) &&
                param.value_names.length === param.values.length
                    ? [...param.value_names]
                    : [...param.values];
            return { values: [...param.values], labels };
        },
        notifyChanged: (element, suppressPromptCompletion = false) => {
            const notify = (): void => triggerChangeFor(element);
            if (suppressPromptCompletion) {
                withSuppressedPromptCompletion(notify);
            } else {
                notify();
            }
        },

        getBase2EditRegistry: () =>
            registrySnapshot(window.base2editStageRegistry),
        getAceStepFunRegistry: () =>
            registrySnapshot(window.acestepfunTrackRegistry),
        getModelCompatId: (modelName) => {
            if (
                typeof modelsHelpers === "undefined" ||
                !modelsHelpers ||
                typeof modelsHelpers.getDataFor !== "function"
            ) {
                return null;
            }
            const id = modelsHelpers.getDataFor("Stable-Diffusion", modelName)
                ?.modelClass?.compatClass?.id;
            return typeof id === "string" && id.trim() ? id : null;
        },
        getModelClassId: (modelName) => {
            if (
                typeof modelsHelpers === "undefined" ||
                !modelsHelpers ||
                typeof modelsHelpers.getDataFor !== "function"
            ) {
                return null;
            }
            const id = modelsHelpers.getDataFor("Stable-Diffusion", modelName)
                ?.modelClass?.id;
            return typeof id === "string" && id.trim() ? id : null;
        },
        getCurrentModelCompatId: () => {
            if (
                typeof currentModelHelper === "undefined" ||
                !currentModelHelper?.curCompatClass ||
                typeof modelsHelpers === "undefined" ||
                !modelsHelpers?.compatClasses
            ) {
                return null;
            }
            const key = currentModelHelper.curCompatClass;
            const compat = modelsHelpers.compatClasses[key];
            const id = compat?.id ?? key;
            return typeof id === "string" && id.trim() ? id : null;
        },
        requestJson: (url, data = {}) =>
            new Promise((resolve, reject) => {
                if (typeof genericRequest !== "function") {
                    reject(new Error("Swarm genericRequest is unavailable."));
                    return;
                }
                genericRequest(
                    url,
                    data,
                    (response) => resolve(response),
                    0,
                    (error) => reject(error),
                );
            }),

        registerPromptPrefix: (
            prefix: string,
            description: string,
            examples: PromptPrefixExamples,
            isMulti: boolean,
        ) => {
            if (typeof promptTabComplete === "undefined") {
                return;
            }
            promptTabComplete.registerPrefix(
                prefix,
                description,
                examples,
                isMulti,
            );
        },
        addPostParamBuildStep: (step) => {
            if (
                typeof postParamBuildSteps === "undefined" ||
                !Array.isArray(postParamBuildSteps)
            ) {
                return false;
            }
            postParamBuildSteps.push(step);
            return true;
        },
        addParamRefreshHook: (hook) => {
            if (
                typeof refreshParamsExtra === "undefined" ||
                !Array.isArray(refreshParamsExtra)
            ) {
                return null;
            }
            refreshParamsExtra.push(hook);
            return () => {
                if (
                    typeof refreshParamsExtra === "undefined" ||
                    !Array.isArray(refreshParamsExtra)
                ) {
                    return;
                }
                const index = refreshParamsExtra.indexOf(hook);
                if (index >= 0) {
                    refreshParamsExtra.splice(index, 1);
                }
            };
        },

        getMediaOutputPrefix: () =>
            typeof getImageOutPrefix === "function" ? getImageOutPrefix() : "",
        createSourceVideoElement: () => document.createElement("video"),
        enableSliders: (element) => {
            if (typeof enableSlidersIn === "function") {
                enableSlidersIn(element);
            }
        },

        registerRefineVideoButton: (onSelect, description) => {
            if (typeof registerMediaButton !== "function") {
                return;
            }
            registerMediaButton(
                "Refine Video",
                onSelect,
                description,
                ["video"],
                true,
            );
        },
        getCurrentMediaMetadata: () =>
            typeof currentMetadataVal === "string" ? currentMetadataVal : null,
        interpretMediaMetadata: (metadata) =>
            typeof interpretMetadata === "function"
                ? interpretMetadata(metadata)
                : metadata,
        showError: (message) => {
            if (typeof showError === "function") {
                showError(message);
            }
        },
        toDataUrl: (src) =>
            new Promise((resolve) => {
                if (typeof toDataURL !== "function") {
                    resolve(src);
                    return;
                }
                toDataURL(src, resolve);
            }),
        generate: (inputOverrides) => {
            if (
                typeof mainGenHandler !== "undefined" &&
                typeof mainGenHandler?.doGenerate === "function"
            ) {
                mainGenHandler.doGenerate(inputOverrides, {});
            }
        },
    });
