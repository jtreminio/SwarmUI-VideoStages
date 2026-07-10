import { audioSource } from "./audioSource";
import { injectTimelineTab } from "./bottomTimelineTab";
import { videoStagesTimeline } from "./videoStagesTimeline";

const timeline = videoStagesTimeline();

const registerVideoStagesPromptPrefix = (): void => {
    if (typeof promptTabComplete === "undefined") {
        return;
    }

    promptTabComplete.registerPrefix(
        "videoclip",
        "Per-clip prompt sections and prompt windows for the VideoStages timeline.",
        () => [
            "\n<videoclip[0]>clip 0's prompt text — everything until the next <videoclip...> tag.",
            "\n<videoclip[0]:1.5-4>a prompt window on clip 0 from 1.5s to 4s.",
            "\nThe timeline owns these; structured config (stages, refs, audio) rides in the hidden Data param.",
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
