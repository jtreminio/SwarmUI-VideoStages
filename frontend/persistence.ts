import { assignMissingHues } from "./clipColor";
import { ROOT_DIMENSION_MIN, ROOT_FPS_MIN } from "./constants";
import { videoStagesDebugLog } from "./debugLog";
import { buildDefaultClip, normalizeClip } from "./normalization";
import { parseClipPrompts } from "./promptSegments";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import {
    getPromptInput,
    isImageToVideoWorkflow,
    isVideoStagesEnabled,
    readDataParam,
    writeClipPrompts,
    writeDataParam,
} from "./swarmInputs";
import type { Clip, StoredClip, VideoStagesConfig } from "./types";
import { applyUiState, saveUiState } from "./uiState";
import { utils } from "./utils";

type InheritedDims = Pick<VideoStagesConfig, "width" | "height" | "fps">;
type RootDims = Pick<
    VideoStagesConfig,
    "width" | "height" | "fps" | "dimsExplicit" | "fpsExplicit"
>;

const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

const toIntOrNull = (value: unknown): number | null => {
    if (value == null || value === "") {
        return null;
    }
    const num = utils.toNumber(`${value}`, Number.NaN);
    return Number.isFinite(num) ? Math.round(num) : null;
};

const resolveRootDims = (
    inherited: InheritedDims,
    stored: { width?: unknown; height?: unknown; fps?: unknown },
): RootDims => {
    const width = toIntOrNull(stored.width);
    const height = toIntOrNull(stored.height);
    const dimsExplicit =
        width !== null &&
        width >= ROOT_DIMENSION_MIN &&
        height !== null &&
        height >= ROOT_DIMENSION_MIN;
    const fps = toIntOrNull(stored.fps);
    const fpsExplicit = fps !== null && fps >= ROOT_FPS_MIN;
    return {
        width: dimsExplicit ? (width as number) : inherited.width,
        height: dimsExplicit ? (height as number) : inherited.height,
        fps: fpsExplicit ? (fps as number) : inherited.fps,
        dimsExplicit,
        fpsExplicit,
    };
};

const rootConfig = (dims: RootDims, clips: Clip[]): VideoStagesConfig => ({
    ...dims,
    clips,
});

export const serializeClipsForStorage = (clips: Clip[]): StoredClip[] =>
    clips.map(
        (clip): StoredClip => ({
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
                source: ref.source,
                uploadFileName: ref.uploadFileName,
                uploadedImage: ref.uploadedImage,
                frame: ref.frame,
                fromEnd: ref.fromEnd,
            })),
            stages: clip.stages.map((stage) => ({
                skipped: stage.skipped,
                control: stage.control,
                controlNetStrength: stage.controlNetStrength,
                refStrengths: stage.refStrengths,
                upscale: stage.upscale,
                upscaleMethod: stage.upscaleMethod,
                model: stage.model,
                steps: stage.steps,
                cfgScale: stage.cfgScale,
                sampler: stage.sampler,
                scheduler: stage.scheduler,
            })),
        }),
    );

export const serializeStateForStorage = (
    state: Pick<
        VideoStagesConfig,
        "width" | "height" | "fps" | "dimsExplicit" | "fpsExplicit" | "clips"
    >,
): string => {
    const out: Record<string, unknown> = {};
    if (state.dimsExplicit) {
        out.width = Math.round(state.width);
        out.height = Math.round(state.height);
    }
    if (state.fpsExplicit) {
        out.fps = Math.round(state.fps);
    }
    out.clips = serializeClipsForStorage(state.clips);
    return JSON.stringify(out);
};

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

const overlayPromptAndUiState = (clips: Clip[]): void => {
    const { sections, windows } = parseClipPrompts(
        getPromptInput()?.value ?? "",
    );
    for (let i = 0; i < clips.length; i++) {
        clips[i].prompt = sections.get(i) ?? "";
        clips[i].promptWindows = (windows.get(i) ?? []).map((window) => ({
            prompt: window.prompt,
            start: window.start,
            duration: window.duration,
        }));
    }
    applyUiState(clips);
    assignMissingHues(clips);
};

const parseSerializedState = (
    serialized: string,
    inherited: InheritedDims,
): VideoStagesConfig | null => {
    try {
        const parsed: unknown = JSON.parse(serialized);
        let clipsRaw: unknown[];
        let stored: { width?: unknown; height?: unknown; fps?: unknown } = {};
        if (Array.isArray(parsed)) {
            clipsRaw = parsed;
        } else if (isRecord(parsed)) {
            clipsRaw = Array.isArray(parsed.clips) ? parsed.clips : [];
            stored = {
                width: parsed.width,
                height: parsed.height,
                fps: parsed.fps,
            };
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
        overlayPromptAndUiState(clips);
        return rootConfig(resolveRootDims(inherited, stored), clips);
    } catch {
        return null;
    }
};

export const getState = (): VideoStagesConfig => {
    const defaults = getRootDefaults();
    const inherited: InheritedDims = {
        width: defaults.width,
        height: defaults.height,
        fps: defaults.fps,
    };
    const serialized = readDataParam() || lastSerializedState;
    if (!serialized) {
        const clips: Clip[] = [];
        overlayPromptAndUiState(clips);
        return rootConfig(resolveRootDims(inherited, {}), clips);
    }

    let parsedState = parseSerializedState(serialized, inherited);
    if (parsedState) {
        lastSerializedState = serialized;
        return parsedState;
    }
    if (serialized !== lastSerializedState && lastSerializedState) {
        parsedState = parseSerializedState(lastSerializedState, inherited);
        if (parsedState) {
            return parsedState;
        }
    }
    return rootConfig(resolveRootDims(inherited, {}), []);
};

export const saveState = (
    state: VideoStagesConfig,
    callbacks?: PersistenceCallbacks,
    options?: SaveStateOptions,
): void => {
    assignMissingHues(state.clips);
    const serialized = serializeStateForStorage(state);
    lastSerializedState = serialized;
    const willNotifyDom = options?.notifyDomChange !== false;
    writeDataParam(serialized, willNotifyDom);
    writeClipPrompts(
        state.clips.map((clip) => ({
            prompt: clip.prompt,
            windows: clip.promptWindows,
        })),
        willNotifyDom,
    );
    saveUiState(state.clips);
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
