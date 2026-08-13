import { isRecord } from "../utils";
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

const requestJson = (
    url: string,
    data: Record<string, unknown> = {},
): Promise<unknown> =>
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
    });

const collectGenerationInput = (
    inputOverrides: Record<string, unknown>,
): Record<string, unknown> => {
    if (
        typeof mainGenHandler === "undefined" ||
        typeof mainGenHandler?.getGenInput !== "function"
    ) {
        throw new Error("Swarm generation input collection is unavailable.");
    }
    const { extra_metadata: requestedExtraMetadata, ...generationOverrides } =
        inputOverrides;
    const actualInput = mainGenHandler.getGenInput(generationOverrides, {});
    if (isRecord(requestedExtraMetadata)) {
        actualInput.extra_metadata = {
            ...(isRecord(actualInput.extra_metadata)
                ? actualInput.extra_metadata
                : {}),
            ...structuredClone(requestedExtraMetadata),
        };
    }
    return actualInput;
};

type ComfyWorkflowWindow = Window & {
    app?: { loadApiJson: (workflow: unknown) => void };
    LiteGraph?: { cloneObject: (workflow: unknown) => unknown };
};

const loadWorkflowInComfyUi = async (workflowJson: string): Promise<void> => {
    const tab = document.getElementById("maintab_comfyworkflow");
    if (!(tab instanceof HTMLElement)) {
        throw new Error("The ComfyUI tab is unavailable.");
    }
    tab.click();

    const workflow = JSON.parse(workflowJson) as unknown;
    const deadline = Date.now() + 30_000;
    while (Date.now() < deadline) {
        const frame = document.getElementById("comfy_workflow_frame");
        const comfyWindow =
            frame instanceof HTMLIFrameElement
                ? (frame.contentWindow as ComfyWorkflowWindow | null)
                : null;
        if (comfyWindow?.app && comfyWindow.LiteGraph) {
            comfyWindow.app.loadApiJson(
                comfyWindow.LiteGraph.cloneObject(workflow),
            );
            return;
        }
        await new Promise((resolve) => window.setTimeout(resolve, 100));
    }
    throw new Error("Timed out waiting for the ComfyUI tab to load.");
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
        hasBackendFeature: (feature) =>
            typeof currentBackendFeatureSet !== "undefined" &&
            Array.isArray(currentBackendFeatureSet) &&
            currentBackendFeatureSet.includes(feature),
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
        getLoraDefaultWeight: (modelName) => {
            const browserModels =
                typeof sdLoraBrowser !== "undefined"
                    ? sdLoraBrowser?.models
                    : undefined;
            const browserModel =
                browserModels?.[modelName] ??
                browserModels?.[`${modelName}.safetensors`];
            const browserRaw = browserModel?.data?.lora_default_weight;
            const helperRaw =
                typeof modelsHelpers !== "undefined" &&
                modelsHelpers &&
                typeof modelsHelpers.getDataFor === "function"
                    ? modelsHelpers.getDataFor("LoRA", modelName)
                          ?.lora_default_weight
                    : undefined;
            const preferenceRaw =
                typeof loraHelper !== "undefined"
                    ? loraHelper?.loraWeightPref?.[modelName]
                    : undefined;
            const finiteWeight = (
                raw: string | number | undefined,
            ): number | null => {
                const value =
                    typeof raw === "number"
                        ? raw
                        : typeof raw === "string" && raw.trim()
                          ? Number(raw.trim())
                          : Number.NaN;
                return Number.isFinite(value) ? value : null;
            };
            return (
                finiteWeight(browserRaw) ??
                finiteWeight(helperRaw) ??
                finiteWeight(preferenceRaw)
            );
        },
        requestJson,

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
        createInitVideoElement: () => document.createElement("video"),
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
        registerRefineVideoToComfyButton: (onSelect, description) => {
            if (typeof registerMediaButton !== "function") {
                return;
            }
            registerMediaButton(
                "Refine Video to ComfyUI",
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
                const {
                    extra_metadata: requestedExtraMetadata,
                    ...generationOverrides
                } = inputOverrides;
                if (!isRecord(requestedExtraMetadata)) {
                    mainGenHandler.doGenerate(generationOverrides, {});
                    return;
                }
                mainGenHandler.doGenerate(
                    generationOverrides,
                    {},
                    (actualInput: unknown): void => {
                        if (!isRecord(actualInput)) {
                            return;
                        }
                        const collectedExtraMetadata =
                            actualInput.extra_metadata;
                        actualInput.extra_metadata = {
                            ...(isRecord(collectedExtraMetadata)
                                ? collectedExtraMetadata
                                : {}),
                            ...structuredClone(requestedExtraMetadata),
                        };
                    },
                );
            }
        },
        sendToComfyUi: async (inputOverrides) => {
            const actualInput = collectGenerationInput(inputOverrides);
            const response = await requestJson(
                "ComfyGetGeneratedWorkflow",
                actualInput,
            );
            if (!isRecord(response) || typeof response.workflow !== "string") {
                const message =
                    isRecord(response) && typeof response.error === "string"
                        ? response.error
                        : "SwarmUI returned no generated ComfyUI workflow.";
                throw new Error(message);
            }
            await loadWorkflowInComfyUi(response.workflow);
        },
    });
