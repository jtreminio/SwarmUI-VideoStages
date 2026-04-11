/// <reference path="./VideoStageEditor.ts" />

class VideoStages
{
    private stageEditor: VideoStageEditor;
    private readonly visibleParamIds = [
        "rootwidth",
        "rootheight"
    ];

    public constructor(stageEditor: VideoStageEditor)
    {
        this.stageEditor = stageEditor;
        if (!this.stageEditor.tryInstallInactiveReuseGuard()) {
            let interval = setInterval(() => {
                if (this.stageEditor.tryInstallInactiveReuseGuard()) {
                    clearInterval(interval);
                }
            }, 200);
        }
        if (!this.tryRegisterStageEditor()) {
            let interval = setInterval(() => {
                if (this.tryRegisterStageEditor()) {
                    clearInterval(interval);
                }
            }, 200);
        }
        if (!this.tryWrapReuseParameters()) {
            let interval = setInterval(() => {
                if (this.tryWrapReuseParameters()) {
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

    private tryWrapReuseParameters(): boolean
    {
        if (typeof copy_current_image_params != "function") {
            return false;
        }

        let wrappedExisting = copy_current_image_params as typeof copy_current_image_params & { __videoStagesWrapped?: boolean };
        if (wrappedExisting.__videoStagesWrapped) {
            return true;
        }

        let prior = copy_current_image_params;
        let wrapped = (() => {
            let metadataUsesVideoStages = this.currentImageUsesVideoStages();
            prior();
            if (metadataUsesVideoStages === false) {
                this.stageEditor.resetForInactiveReuse();
            }
        }) as typeof copy_current_image_params & { __videoStagesWrapped?: boolean };
        wrapped.__videoStagesWrapped = true;
        copy_current_image_params = wrapped;
        return true;
    }

    private currentImageUsesVideoStages(): boolean | null
    {
        if (!currentMetadataVal) {
            return null;
        }

        try {
            let metadataFull = JSON.parse(interpretMetadata(currentMetadataVal));
            let metadata = metadataFull?.sui_image_params as Record<string, unknown> | undefined;
            if (!metadata || typeof metadata != "object") {
                return null;
            }

            let enabled = metadata.enableadditionalvideostages ?? metadata.enablevideostages;
            if (`${enabled}` == "true") {
                return true;
            }

            if (this.visibleParamIds.some((id) => metadata[id] !== undefined && metadata[id] !== null && metadata[id] !== "")) {
                return true;
            }

            let guideReference = `${metadata.guideimagereference ?? ""}`.trim();
            if (guideReference && guideReference.toLowerCase() != "default") {
                return true;
            }

            // The hidden `videostages` JSON is always seeded locally, so it is not
            // trustworthy evidence that the reused image truly opted into VideoStages.
            return false;
        }
        catch (error) {
            console.log("VideoStages: failed to inspect reused image metadata", error);
            return null;
        }
    }
}

new VideoStages(new VideoStageEditor());
