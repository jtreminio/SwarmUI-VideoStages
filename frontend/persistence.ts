import {
    buildDefaultClip,
    normalizeClip,
    normalizeRootDimension,
    normalizeRootFps,
} from "./normalization";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import {
    getClipsInput,
    getCoreDimension,
    getRegisteredRootDimension,
    getRegisteredRootFps,
} from "./swarmInputs";
import type { Clip, StoredClip, VideoStagesConfig } from "./Types";

type ParsedConfig = {
    width?: unknown;
    height?: unknown;
    fps?: unknown;
    clips?: unknown[];
};

const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

const toParsedConfig = (value: unknown): ParsedConfig | null => {
    if (!isRecord(value)) {
        return null;
    }
    return {
        width: value.width,
        height: value.height,
        fps: value.fps,
        clips: Array.isArray(value.clips) ? value.clips : undefined,
    };
};

export const serializeClipsForStorage = (clips: Clip[]): StoredClip[] =>
    clips.map(
        (clip): StoredClip => ({
            name: clip.name,
            expanded: clip.expanded,
            skipped: clip.skipped,
            duration: clip.duration,
            audioSource: clip.audioSource,
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

const getEffectiveRootDimension = (
    field: "width" | "height",
    persistedValue: unknown,
    fallback: number,
): number =>
    getRegisteredRootDimension(field) ??
    getCoreDimension(field) ??
    normalizeRootDimension(persistedValue, fallback);

export interface PersistenceCallbacks {
    onAfterSerialize?: (serialized: string) => void;
}

export const getState = (): VideoStagesConfig => {
    const defaults = getRootDefaults();
    const input = getClipsInput();
    if (!input?.value) {
        return {
            width: defaults.width,
            height: defaults.height,
            fps: defaults.fps,
            clips: [],
        };
    }

    try {
        const parsed = JSON.parse(input.value);
        const parsedConfig = toParsedConfig(parsed);
        let clipsRaw: unknown[] = [];
        if (Array.isArray(parsed)) {
            clipsRaw = parsed;
        } else if (Array.isArray(parsedConfig?.clips)) {
            clipsRaw = parsedConfig.clips;
        }
        const firstClip =
            clipsRaw.length > 0 && isRecord(clipsRaw[0]) ? clipsRaw[0] : null;

        const clips: Clip[] = [];
        for (let i = 0; i < clipsRaw.length; i++) {
            const el = clipsRaw[i];
            const record: Record<string, unknown> = isRecord(el) ? el : {};
            clips.push(
                normalizeClip(record, i, getRootDefaults, getDefaultStageModel),
            );
        }
        return {
            width: getEffectiveRootDimension(
                "width",
                parsedConfig?.width ?? firstClip?.width,
                defaults.width,
            ),
            height: getEffectiveRootDimension(
                "height",
                parsedConfig?.height ?? firstClip?.height,
                defaults.height,
            ),
            fps:
                getRegisteredRootFps() ??
                normalizeRootFps(parsedConfig?.fps, defaults.fps),
            clips,
        };
    } catch {
        return {
            width: defaults.width,
            height: defaults.height,
            fps: defaults.fps,
            clips: [],
        };
    }
};

export const saveState = (
    state: VideoStagesConfig,
    callbacks?: PersistenceCallbacks,
): void => {
    const input = getClipsInput();
    if (!input) {
        return;
    }

    const serialized = JSON.stringify({
        width: state.width,
        height: state.height,
        fps: state.fps,
        clips: serializeClipsForStorage(state.clips),
    });
    input.value = serialized;
    callbacks?.onAfterSerialize?.(serialized);
    triggerChangeFor(input);
};

export const getClips = (): Clip[] => getState().clips;

export const saveClips = (
    clips: Clip[],
    callbacks?: PersistenceCallbacks,
): void => {
    const state = getState();
    state.clips = clips;
    saveState(state, callbacks);
};

export const ensureClipsSeeded = (callbacks?: PersistenceCallbacks): void => {
    const state = getState();
    if (state.clips.length > 0) {
        return;
    }

    state.clips = [buildDefaultClip(0, getRootDefaults, getDefaultStageModel)];
    saveState(state, callbacks);
};
