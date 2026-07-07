import { videoStagesDebugLog } from "./debugLog";
import { buildDefaultClip, normalizeClip } from "./normalization";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import {
    isImageToVideoWorkflow,
    isVideoStagesEnabled,
    readVideoStagesSection,
    writeVideoStagesSection,
} from "./swarmInputs";
import type { Clip, StoredClip, VideoStagesConfig } from "./types";

type RootDims = Pick<VideoStagesConfig, "width" | "height" | "fps">;

const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

const rootConfig = (dims: RootDims, clips: Clip[]): VideoStagesConfig => ({
    ...dims,
    clips,
});

export const serializeClipsForStorage = (clips: Clip[]): StoredClip[] =>
    clips.map(
        (clip): StoredClip => ({
            expanded: clip.expanded,
            skipped: clip.skipped,
            duration: clip.duration,
            audioSource: clip.audioSource,
            controlNetSource: clip.controlNetSource,
            controlNetLora: clip.controlNetLora,
            saveAudioTrack: clip.saveAudioTrack,
            clipLengthFromAudio: clip.clipLengthFromAudio,
            clipLengthFromControlNet: clip.clipLengthFromControlNet,
            reuseAudio: clip.reuseAudio,
            uploadedAudio: clip.uploadedAudio,
            refs: clip.refs.map((ref) => ({
                expanded: ref.expanded,
                source: ref.source,
                uploadFileName: ref.uploadFileName,
                uploadedImage: ref.uploadedImage,
                frame: ref.frame,
                fromEnd: ref.fromEnd,
            })),
            stages: clip.stages.map((stage) => ({
                expanded: stage.expanded,
                skipped: stage.skipped,
                control: stage.control,
                controlNetStrength: stage.controlNetStrength,
                refStrengths: stage.refStrengths,
                upscale: stage.upscale,
                upscaleMethod: stage.upscaleMethod,
                model: stage.model,
                vae: stage.vae,
                steps: stage.steps,
                cfgScale: stage.cfgScale,
                sampler: stage.sampler,
                scheduler: stage.scheduler,
            })),
        }),
    );

export const serializeStateForStorage = (
    state: Pick<VideoStagesConfig, "clips">,
): string =>
    JSON.stringify({
        clips: serializeClipsForStorage(state.clips),
    });

export interface PersistenceCallbacks {
    onAfterSerialize?: (serialized: string) => void;
}

export interface SaveStateOptions {
    notifyDomChange?: boolean;
}

let lastSerializedState = "";

export const __resetPersistenceForTests = (): void => {
    lastSerializedState = "";
};

const parseSerializedState = (
    serialized: string,
    fallbackDefaults: RootDims,
): VideoStagesConfig | null => {
    try {
        const parsed: unknown = JSON.parse(serialized);
        let clipsRaw: unknown[];
        if (Array.isArray(parsed)) {
            clipsRaw = parsed;
        } else if (isRecord(parsed) && Array.isArray(parsed.clips)) {
            clipsRaw = parsed.clips;
        } else {
            clipsRaw = [];
        }
        const clips = clipsRaw.map((el) =>
            normalizeClip(
                isRecord(el) ? el : {},
                getRootDefaults,
                getDefaultStageModel,
            ),
        );
        return rootConfig(fallbackDefaults, clips);
    } catch {
        return null;
    }
};

export const getState = (): VideoStagesConfig => {
    const defaults = getRootDefaults();
    const serialized = readVideoStagesSection() || lastSerializedState;
    if (!serialized) {
        return rootConfig(defaults, []);
    }

    let parsedState = parseSerializedState(serialized, defaults);
    if (parsedState) {
        lastSerializedState = serialized;
        return parsedState;
    }
    if (serialized !== lastSerializedState && lastSerializedState) {
        parsedState = parseSerializedState(lastSerializedState, defaults);
        if (parsedState) {
            return parsedState;
        }
    }
    return rootConfig(defaults, []);
};

export const saveState = (
    state: VideoStagesConfig,
    callbacks?: PersistenceCallbacks,
    options?: SaveStateOptions,
): void => {
    const serialized = serializeStateForStorage(state);
    lastSerializedState = serialized;
    const willNotifyDom = options?.notifyDomChange !== false;
    writeVideoStagesSection(serialized, willNotifyDom);
    callbacks?.onAfterSerialize?.(serialized);
    videoStagesDebugLog("persistence", "saveState", {
        notifyDomChange: options?.notifyDomChange,
        willNotifyDom,
        jsonChars: serialized.length,
    });
};

export const getClips = (): Clip[] => getState().clips;

export const saveClips = (
    clips: Clip[],
    callbacks?: PersistenceCallbacks,
    options?: SaveStateOptions,
): void => {
    videoStagesDebugLog("persistence", "saveClips", {
        clipCount: clips.length,
    });
    const state = getState();
    state.clips = clips;
    const notifyDomChange =
        options?.notifyDomChange !== undefined
            ? options.notifyDomChange
            : isVideoStagesEnabled();
    saveState(state, callbacks, { ...options, notifyDomChange });
};

export const ensureClipsSeeded = (
    callbacks?: PersistenceCallbacks,
    options?: SaveStateOptions,
): void => {
    const state = getState();
    if (state.clips.length > 0) {
        return;
    }

    state.clips = [
        buildDefaultClip(
            getRootDefaults,
            getDefaultStageModel,
            isImageToVideoWorkflow(),
        ),
    ];
    saveState(state, callbacks, options);
};
