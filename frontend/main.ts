import { injectTimelineTab } from "./bottomTimelineTab";
import { getVideoStagesHostBridge } from "./host";
import { refineVideoButton } from "./refineVideoButton";
import { DATA_INPUT_ID } from "./swarmInputs";
import { videoStagesTimeline } from "./videoStagesTimeline";

const timeline = videoStagesTimeline();

// One-shot diagnostic for the case where the Data input never gets built at
// all (backend extension failed to load): the init gate below skips silently,
// so without this a broken install would produce no signal.
let dataInputWatchdog: ReturnType<typeof setTimeout> | null = null;
const warnIfDataInputNeverAppears = (): void => {
    if (dataInputWatchdog) {
        return;
    }
    dataInputWatchdog = setTimeout(() => {
        if (!getVideoStagesHostBridge().hasElement(DATA_INPUT_ID)) {
            console.warn(
                `VideoStages: Data param input (#${DATA_INPUT_ID}) never appeared — ` +
                    "is the VideoStages backend extension loaded?",
            );
        }
    }, 10000);
};

const registerVideoStagesPromptPrefix = (): void => {
    getVideoStagesHostBridge().registerPromptPrefix(
        "videoclip",
        "Per-clip prompt sections and prompt windows for the VideoStages timeline.",
        () => [
            "\n<videoclip[0]>the first clip's prompt text — everything until the next <videoclip...> tag.",
            "\n<videoclip[0]:1.5-4>a prompt window on the first clip from 1.5s to 4s.",
            "\nThe timeline owns these; structured config (stages, refs, audio) rides in the hidden Data param.",
        ],
        true,
    );
};

const DATA_INPUT_RETRY_MS = 250;
let dataInputRetryTimer: ReturnType<typeof setTimeout> | null = null;

const initTimeline = (): void => {
    // The Data input is built by genpage's async param build, which can land
    // after (or without re-running) the postParamBuildSteps pass that calls
    // us. Initializing against a paramless DOM would parse an empty carrier
    // and silently no-op every save, so keep retrying until the input exists;
    // init is re-runnable by design, so a later host-triggered call is fine.
    if (!getVideoStagesHostBridge().hasElement(DATA_INPUT_ID)) {
        warnIfDataInputNeverAppears();
        if (!dataInputRetryTimer) {
            dataInputRetryTimer = setTimeout(() => {
                dataInputRetryTimer = null;
                initTimeline();
            }, DATA_INPUT_RETRY_MS);
        }
        return;
    }
    try {
        timeline.init();
    } catch (error) {
        console.warn("VideoStages: failed to init timeline", error);
    }
};

getVideoStagesHostBridge().addPostParamBuildStep(initTimeline);
registerVideoStagesPromptPrefix();
refineVideoButton();
injectTimelineTab();
