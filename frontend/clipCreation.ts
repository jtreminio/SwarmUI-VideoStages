import {
    architectureForModel,
    loadAuthoritativeArchitectureCatalog,
} from "./architectures/catalog";
import { captureAuthoringTransactionSnapshot } from "./authoringSnapshot";
import { getVideoStagesHostBridge } from "./host";
import { buildDefaultClip } from "./normalizationClip";
import { getState, saveState } from "./persistence/repository";
import { getDefaultStageModel } from "./rootDefaults";
import { setSelection } from "./selection";

let addClipInFlight = false;

const addClipAfterCatalog = async (): Promise<void> => {
    try {
        await loadAuthoritativeArchitectureCatalog();
        const { defaults } = captureAuthoringTransactionSnapshot();
        const defaultModel = getDefaultStageModel(defaults);
        if (
            !defaultModel ||
            architectureForModel(defaults.modelCatalog, defaultModel) === null
        ) {
            getVideoStagesHostBridge().showError(
                "VideoStages cannot add a clip because no supported video model is available.",
            );
            return;
        }

        const state = getState();
        const clips = state.clips;
        const previousClip = clips[clips.length - 1] ?? null;
        if (previousClip && clips.length >= 2) {
            const previousJoin = clips[clips.length - 2];
            previousClip.boundaryOut = previousJoin.boundaryOut;
            previousClip.boundaryOutCarryAudio =
                previousJoin.boundaryOutCarryAudio;
            previousClip.boundaryOutReferenceScale =
                previousJoin.boundaryOutReferenceScale;
            previousClip.boundaryOutReferenceIncludeSoundtrack =
                previousJoin.boundaryOutReferenceIncludeSoundtrack;
            previousClip.boundaryOutOverlap = previousJoin.boundaryOutOverlap;
        }
        clips.push(
            buildDefaultClip(defaults, defaultModel, false, previousClip),
        );
        saveState(state, { origin: "timeline" });
        setSelection({
            kind: "clip",
            clipIdx: clips.length - 1,
            stageIdx: 0,
        });
    } catch (error) {
        console.warn("VideoStages: failed to add clip", error);
        getVideoStagesHostBridge().showError(
            "VideoStages could not add the clip. See the browser console for details.",
        );
    } finally {
        addClipInFlight = false;
    }
};

export const addVideoStagesClip = (): void => {
    if (addClipInFlight) {
        return;
    }
    addClipInFlight = true;
    void addClipAfterCatalog();
};
