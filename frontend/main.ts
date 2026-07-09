import { audioSource } from "./audioSource";
import { injectTimelineTab } from "./bottomTimelineTab";
import { videoStagesTimeline } from "./videoStagesTimeline";

const timeline = videoStagesTimeline();

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
            '\nPer-clip "prompt" and per-clip / per-stage "loras" fold into this JSON — there is no more <videoclip> section.',
        ],
        true,
    );
};

const initTimeline = (): void => {
    try {
        timeline.init();
    } catch (error) {
        console.warn("VideoStages: failed to init timeline", error);
    }
};

const scheduleTimelineInit = (): void => {
    if (!Array.isArray(postParamBuildSteps)) {
        setTimeout(scheduleTimelineInit, 200);
        return;
    }
    postParamBuildSteps.push(initTimeline);
};

scheduleTimelineInit();
registerVideoStagesPromptPrefix();
audioSource();
injectTimelineTab();
