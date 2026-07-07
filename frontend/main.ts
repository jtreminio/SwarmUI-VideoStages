import { audioSource } from "./audioSource";
import {
    applyVideoStagesPresetDimensionsBeforeGenerate,
    wireDimensionsPreset,
} from "./dimensionsDropdown";
import {
    isVideoStagesEnabled,
    seedRegisteredDimensionsFromCore,
} from "./swarmInputs";

const registerVideoStagesPromptPrefix = (): void => {
    if (typeof promptTabComplete === "undefined") {
        return;
    }

    promptTabComplete.registerPrefix(
        "videostages",
        "Configure all VideoStages settings as one JSON prompt section.",
        () => [
            '\nUse "<videostages>{ ...JSON... }" to configure clips, stages, refs, audio, prompts and loras in one JSON blob.',
            '\nExample: <videostages>{"clips":[{"prompt":"a red fox","stages":[{"model":"...","steps":30}]}]}',
            '\nPer-clip / per-stage "prompt" and "loras" fold into this JSON — there is no more <videoclip> section.',
        ],
        true,
    );
};

const initDimensions = (): void => {
    try {
        seedRegisteredDimensionsFromCore(isVideoStagesEnabled());
        wireDimensionsPreset();
    } catch (error) {
        console.warn("VideoStages: failed to init dimensions", error);
    }
};

const scheduleDimensionsInit = (): void => {
    if (!Array.isArray(postParamBuildSteps)) {
        setTimeout(scheduleDimensionsInit, 200);
        return;
    }
    postParamBuildSteps.push(initDimensions);
};

const wrapGenerateForDimensions = (): boolean => {
    if (
        typeof mainGenHandler === "undefined" ||
        !mainGenHandler ||
        typeof mainGenHandler.doGenerate !== "function"
    ) {
        return false;
    }
    const original = mainGenHandler.doGenerate.bind(mainGenHandler);
    mainGenHandler.doGenerate = (...args: unknown[]) => {
        if (isVideoStagesEnabled()) {
            applyVideoStagesPresetDimensionsBeforeGenerate();
        }
        return original(...args);
    };
    return true;
};

const scheduleGenerateWrap = (): void => {
    if (wrapGenerateForDimensions()) {
        return;
    }
    const interval = setInterval(() => {
        if (wrapGenerateForDimensions()) {
            clearInterval(interval);
        }
    }, 250);
};

scheduleDimensionsInit();
scheduleGenerateWrap();
registerVideoStagesPromptPrefix();
audioSource();
