/// <reference path="./VideoStageEditor.ts" />

class VideoStages
{
    private stageEditor: VideoStageEditor;

    public constructor(stageEditor: VideoStageEditor)
    {
        this.stageEditor = stageEditor;
        if (!this.tryRegisterStageEditor()) {
            let interval = setInterval(() => {
                if (this.tryRegisterStageEditor()) {
                    clearInterval(interval);
                }
            }, 200);
        }
        this.stageEditor.startGenerateWrapRetry();
    }

    private tryRegisterStageEditor(): boolean
    {
        if (typeof postParamBuildSteps == "undefined" || !Array.isArray(postParamBuildSteps)) {
            return false;
        }

        postParamBuildSteps.push(() => {
            try {
                this.stageEditor.init();
            }
            catch (error) {
                console.log("VideoStages: failed to build stage editor", error);
            }
        });
        return true;
    }
}

new VideoStages(new VideoStageEditor());
