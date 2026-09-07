import { createClipEditor } from "./clipEditor/clipEditor";
import { mountClipEditor } from "./clipEditor/tab";
import { materializeGenerationMedia } from "./generationMedia";
import { getVideoStagesHostBridge } from "./host";
import { installHostGenerationInputProjector } from "./host/swarmUiAdapters";
import { refineVideoButton } from "./refineVideoButton";
import { DATA_INPUT_ID } from "./swarmInputs";
import { createVideoStagesApplication } from "./videoStagesApplication";

const application = createVideoStagesApplication(createClipEditor());

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
        "Per-clip prompt sections and prompt windows for VideoStages.",
        () => [
            "\n<videoclip[0]>the first clip's prompt text — everything until the next <videoclip...> tag.",
            "\n<videoclip[0]:1.5-4>a prompt window on the first clip from 1.5s to 4s.",
            "\nVideoStages owns these; structured config (stages, references, keyframes, audio) rides in the hidden Data param.",
        ],
        true,
    );
};

const DATA_INPUT_RETRY_MS = 250;
let dataInputRetryTimer: ReturnType<typeof setTimeout> | null = null;

const initVideoStages = (): void => {
    // Host parameter creation is asynchronous; persistence needs the Data carrier.
    if (!getVideoStagesHostBridge().hasElement(DATA_INPUT_ID)) {
        warnIfDataInputNeverAppears();
        if (!dataInputRetryTimer) {
            dataInputRetryTimer = setTimeout(() => {
                dataInputRetryTimer = null;
                initVideoStages();
            }, DATA_INPUT_RETRY_MS);
        }
        return;
    }
    try {
        application.init();
        installHostGenerationInputProjector(materializeGenerationMedia);
    } catch (error) {
        console.warn("VideoStages: failed to initialize editor", error);
    }
};

getVideoStagesHostBridge().addPostParamBuildStep(initVideoStages);
registerVideoStagesPromptPrefix();
refineVideoButton();
mountClipEditor();
