import { AudioSourceController } from "./AudioSourceController";
import { VideoStageEditor } from "./VideoStageEditor";

const stageEditor = VideoStageEditor();

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

stageEditor.startGenerateWrapRetry();

AudioSourceController();
