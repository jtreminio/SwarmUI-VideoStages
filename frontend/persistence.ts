import { assignMissingHues } from "./clipColor";
import { ROOT_DIMENSION_MIN, ROOT_FPS_MIN } from "./constants";
import { videoStagesDebugLog } from "./debugLog";
import { normalizeClip } from "./normalization";
import { parseClipPrompts } from "./promptSegments";
import {
    getDefaultStageModel,
    getRootDefaults,
    readInheritedDimsSignature,
} from "./rootDefaults";
import {
    createTimelineStore,
    type TimelineStore,
    type UpdateOrigin,
} from "./store";
import {
    getPromptInput,
    isVideoStagesEnabled,
    notifyCarrierChanged,
    readDataParam,
    readStateToken,
    writeClipPrompts,
    writeDataParam,
} from "./swarmInputs";
import type { Clip, StoredClip, VideoStagesConfig } from "./types";
import { applyUiState, saveUiState } from "./uiState";
import { isRecord, utils } from "./utils";

type InheritedDims = Pick<VideoStagesConfig, "width" | "height" | "fps">;
type RootDims = Pick<
    VideoStagesConfig,
    "width" | "height" | "fps" | "dimsExplicit" | "fpsExplicit"
>;

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
            boundaryOut: clip.boundaryOut,
            duration: clip.duration,
            audioSource: clip.audioSource,
            icLoras: clip.icLoras.map((entry) => ({
                lora: entry.lora,
                preset: entry.preset,
                source: entry.source,
                stage: entry.stage,
                strength: entry.strength,
                attentionStrength: entry.attentionStrength,
                controlType: entry.controlType,
                video: entry.video,
                driveAudioRef: entry.driveAudioRef,
            })),
            saveAudioTrack: clip.saveAudioTrack,
            clipLengthFromAudio: clip.clipLengthFromAudio,
            clipLengthFromControlNet: clip.clipLengthFromControlNet,
            reuseAudio: clip.reuseAudio,
            uploadedAudio: clip.uploadedAudio,
            audioSegments: clip.audioSegments.map((seg) => ({
                source: seg.source,
                startSeconds: seg.startSeconds,
                trimStartSeconds: seg.trimStartSeconds,
                lengthSeconds: seg.lengthSeconds,
            })),
            retake: clip.retake
                ? {
                      startSeconds: clip.retake.startSeconds,
                      lengthSeconds: clip.retake.lengthSeconds,
                      strength: clip.retake.strength,
                  }
                : null,
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
                loras: stage.loras.map((lora) => ({
                    name: lora.name,
                    weight: lora.weight,
                })),
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

export interface SaveStateOptions {
    notifyDomChange?: boolean;
    /** Which component committed this save; threaded to store subscribers. */
    origin?: UpdateOrigin;
    /**
     * A dock value edit that changes data but not panel structure — threaded
     * to subscribers as UpdateMeta.hint so the dock skips its rebuild.
     */
    valueOnly?: boolean;
}

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

const inheritedDims = (): InheritedDims => {
    const defaults = getRootDefaults();
    return {
        width: defaults.width,
        height: defaults.height,
        fps: defaults.fps,
    };
};

const parseEmptyConfig = (): VideoStagesConfig => {
    const clips: Clip[] = [];
    overlayPromptAndUiState(clips);
    return rootConfig(resolveRootDims(inheritedDims(), {}), clips);
};

/** Serialize + write both carriers without host change events (store save path). */
const writeQuietly = (state: VideoStagesConfig): string => {
    assignMissingHues(state.clips);
    const serialized = serializeStateForStorage(state);
    writeDataParam(serialized);
    writeClipPrompts(
        state.clips.map((clip) => ({
            prompt: clip.prompt,
            windows: clip.promptWindows,
        })),
    );
    saveUiState(state.clips);
    return serialized;
};

const store = createTimelineStore({
    readToken: () => `${readStateToken()}\x00${readInheritedDimsSignature()}`,
    readDataParam,
    parse: (serialized) => parseSerializedState(serialized, inheritedDims()),
    parseEmpty: parseEmptyConfig,
    writeQuiet: writeQuietly,
    notifyHost: notifyCarrierChanged,
});

/** The store singleton — subscription/sync surface for the orchestrator. */
export const getTimelineStore = (): TimelineStore => store;

export const __resetPersistenceForTests = (): void => {
    store.resetForTests();
};

export const getState = (): VideoStagesConfig => store.getState();

export const saveState = (
    state: VideoStagesConfig,
    options?: SaveStateOptions,
): void => {
    const willNotifyDom = options?.notifyDomChange !== false;
    const serialized = store.save(
        state,
        options?.origin ?? "timeline",
        willNotifyDom,
        options?.valueOnly ? "value-only" : undefined,
    );
    videoStagesDebugLog("persistence", "saveState", {
        notifyDomChange: options?.notifyDomChange,
        willNotifyDom,
        jsonChars: serialized.length,
    });
};

export const getClips = (): Clip[] => getState().clips;

export const saveClips = (clips: Clip[], options?: SaveStateOptions): void => {
    videoStagesDebugLog("persistence", "saveClips", {
        clipCount: clips.length,
    });
    const state = getState();
    state.clips = clips;
    const notifyDomChange =
        options?.notifyDomChange !== undefined
            ? options.notifyDomChange
            : isVideoStagesEnabled();
    saveState(state, { ...options, notifyDomChange });
};
