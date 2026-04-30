import { audioSource } from "./audioSource";
import { videoStageEditor } from "./videoStageEditor";

const stageEditor = videoStageEditor();

const registerVideoClipPromptPrefix = (): void => {
    if (typeof promptTabComplete === "undefined") {
        return;
    }

    promptTabComplete.registerPrefix(
        "videoclip",
        "Add a prompt section that applies to VideoStages clips.",
        () => [
            '\nUse "<videoclip>..." to apply to ALL VideoStages clips (including LoRAs inside the section).',
            '\nUse "<videoclip[0]>..." to apply to clip 0, "<videoclip[1]>..." for clip 1, etc.',
            '\nUse "<videoclip[0,0]>..." for clip 0 stage 0 only, e.g. "<videoclip[1,2]>" for clip 1 stage 2.',
            '\nIf no "<videoclip>" / "<videoclip[0]>" section exists for a clip, VideoStages falls back to the global prompt.',
        ],
        true,
    );
};

const tryRegisterStageEditor = (): boolean => {
    if (!Array.isArray(postParamBuildSteps)) {
        return false;
    }

    postParamBuildSteps.push(() => {
        try {
            stageEditor.init();
        } catch (error) {
            console.warn("VideoStages: failed to build stage editor", error);
        }
    });
    return true;
};

if (!tryRegisterStageEditor()) {
    const interval = setInterval(() => {
        if (tryRegisterStageEditor()) {
            clearInterval(interval);
        }
    }, 200);
}

registerVideoClipPromptPrefix();

stageEditor.startGenerateWrapRetry();

audioSource();
