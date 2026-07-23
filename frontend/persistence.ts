import { assignMissingHues } from "./clipColor";
import { ROOT_DIMENSION_MIN, ROOT_FPS_MIN } from "./constants";
import { videoStagesDebugLog } from "./debugLog";
import type { DocumentCommand } from "./documentCommands";
import { diffDocuments } from "./documentDiff";
import {
    ensureAuthoringDocumentIdentity,
    ensureClipEntityIdentities,
} from "./identity";
import { normalizeAudioTracks, normalizeClip } from "./normalization";
import { parseClipPrompts } from "./promptSegments";
import {
    getDefaultStageModel,
    getRootDefaults,
    readInheritedDimsSignature,
} from "./rootDefaults";
import {
    createTimelineStore,
    type TimelineDispatchResult,
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
import {
    type CanonicalClip,
    type Clip,
    CURRENT_AUTHORING_SCHEMA_VERSION,
    type StoredClip,
    type VideoStagesConfig,
} from "./types";
import { applyUiState, saveUiState } from "./uiState";
import { isRecord, toNumber } from "./utils";

type InheritedDims = Pick<VideoStagesConfig, "width" | "height" | "fps">;
type RootDims = Pick<
    VideoStagesConfig,
    "width" | "height" | "fps" | "dimsExplicit" | "fpsExplicit"
>;

const toIntOrNull = (value: unknown): number | null => {
    if (value == null || value === "") {
        return null;
    }
    const num = toNumber(`${value}`, Number.NaN);
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

const rootConfig = (
    dims: RootDims,
    clips: Clip[],
    audioTracks: VideoStagesConfig["audioTracks"] = [],
): VideoStagesConfig => {
    const config: VideoStagesConfig = {
        schemaVersion: CURRENT_AUTHORING_SCHEMA_VERSION,
        ...dims,
        clips,
        audioTracks,
    };
    ensureAuthoringDocumentIdentity(config);
    return config;
};

export const serializeClipsForStorage = (clips: Clip[]): StoredClip[] => {
    ensureClipEntityIdentities(clips);
    return (clips as CanonicalClip[]).map(
        (clip): StoredClip => ({
            id: clip.id,
            skipped: clip.skipped,
            boundaryOut: clip.boundaryOut,
            boundaryOutOverlap: clip.boundaryOutOverlap,
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
            sourceVideo: clip.sourceVideo
                ? {
                      data: clip.sourceVideo.data,
                      fileName: clip.sourceVideo.fileName,
                      fps: clip.sourceVideo.fps,
                      durationSeconds: clip.sourceVideo.durationSeconds,
                      startSeconds: clip.sourceVideo.startSeconds,
                      lengthSeconds: clip.sourceVideo.lengthSeconds,
                  }
                : null,
            audioSegments: clip.audioSegments.map((seg) => ({
                id: seg.id,
                source: seg.source,
                startSeconds: seg.startSeconds,
                trimStartSeconds: seg.trimStartSeconds,
                lengthSeconds: seg.lengthSeconds,
            })),
            retake: clip.retake
                ? {
                      id: clip.retake.id,
                      startSeconds: clip.retake.startSeconds,
                      lengthSeconds: clip.retake.lengthSeconds,
                      strength: clip.retake.strength,
                  }
                : null,
            refs: clip.refs.map((ref) => ({
                id: ref.id,
                source: ref.source,
                uploadFileName: ref.uploadFileName,
                uploadedImage: ref.uploadedImage,
                frame: ref.frame,
                fromEnd: ref.fromEnd,
            })),
            stages: clip.stages.map((stage) => ({
                id: stage.id,
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
};

export const serializeStateForStorage = (state: VideoStagesConfig): string => {
    ensureAuthoringDocumentIdentity(state);
    const out: Record<string, unknown> = {
        schemaVersion: CURRENT_AUTHORING_SCHEMA_VERSION,
    };
    if (state.dimsExplicit) {
        out.width = Math.round(state.width);
        out.height = Math.round(state.height);
    }
    if (state.fpsExplicit) {
        out.fps = Math.round(state.fps);
    }
    out.clips = serializeClipsForStorage(state.clips);
    out.audioTracks = state.audioTracks.map((track) => ({
        id: track.id,
        source: {
            kind: track.source.kind,
            reference: track.source.reference,
            uploadedAudio: track.source.uploadedAudio,
        },
        spans: track.spans.map((span) => ({
            id: span.id,
            firstClipId: span.firstClipId,
            lastClipId: span.lastClipId,
            timelineStartSeconds: span.timelineStartSeconds,
            timelineLengthSeconds: span.timelineLengthSeconds,
            sourceStartSeconds: span.sourceStartSeconds,
            clipStartOffsetSeconds: span.clipStartOffsetSeconds,
            clipLengthSeconds: span.clipLengthSeconds,
        })),
    }));
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
    /** Optional compare-and-swap guard from TimelineStore.getSnapshot(). */
    expectedRevision?: number;
}

export type DispatchDocumentCommandOptions = SaveStateOptions;

const overlayPromptAndUiState = (clips: Clip[]): void => {
    // Data-carried clip IDs must exist before the browser-local prompt/UI
    // sidecar is matched; prompt-window IDs themselves are restored below.
    ensureClipEntityIdentities(clips);
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
    ensureClipEntityIdentities(clips);
    assignMissingHues(clips);
};

const parseSerializedState = (
    serialized: string,
    inherited: InheritedDims,
): VideoStagesConfig | null => {
    try {
        const parsed: unknown = JSON.parse(serialized);
        let clipsRaw: unknown[];
        let stored: {
            width?: unknown;
            height?: unknown;
            fps?: unknown;
            audioTracks?: unknown;
        } = {};
        if (Array.isArray(parsed)) {
            clipsRaw = parsed;
        } else if (isRecord(parsed)) {
            clipsRaw = Array.isArray(parsed.clips) ? parsed.clips : [];
            stored = {
                width: parsed.width,
                height: parsed.height,
                fps: parsed.fps,
                audioTracks: parsed.audioTracks,
            };
        } else {
            clipsRaw = [];
        }
        const dims = resolveRootDims(inherited, stored);
        const clips = clipsRaw.map((el) =>
            normalizeClip(
                isRecord(el) ? el : {},
                getRootDefaults,
                getDefaultStageModel,
                dims.fps,
            ),
        );
        overlayPromptAndUiState(clips);
        return rootConfig(
            dims,
            clips,
            normalizeAudioTracks(stored.audioTracks),
        );
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
    ensureAuthoringDocumentIdentity(state);
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

const throwSaveFailure = (
    phase: "diff" | "dispatch",
    error: unknown,
): never => {
    const detail =
        error instanceof Error
            ? `${error.name}: ${error.message}`
            : String(error);
    console.error(`[VideoStages persistence] saveState ${phase} failed`, error);
    videoStagesDebugLog("persistence", `saveState ${phase} failed`, {
        detail,
    });
    throw new Error(`VideoStages saveState ${phase} failed: ${detail}`, {
        cause: error,
    });
};

const hasCanonicalStoredId = (
    value: unknown,
    seen: Set<string>,
): value is Record<string, unknown> & { id: string } => {
    if (
        !isRecord(value) ||
        typeof value.id !== "string" ||
        value.id.length === 0 ||
        value.id.trim() !== value.id ||
        seen.has(value.id)
    ) {
        return false;
    }
    seen.add(value.id);
    return true;
};

const dataCarrierNeedsCanonicalMigration = (): boolean => {
    try {
        const parsed: unknown = JSON.parse(readDataParam());
        if (
            !isRecord(parsed) ||
            parsed.schemaVersion !== CURRENT_AUTHORING_SCHEMA_VERSION ||
            !Array.isArray(parsed.clips) ||
            !Array.isArray(parsed.audioTracks)
        ) {
            return true;
        }
        const seenIds = new Set<string>();
        for (const rawClip of parsed.clips) {
            if (!hasCanonicalStoredId(rawClip, seenIds)) return true;
            for (const key of ["stages", "refs", "audioSegments"] as const) {
                const children = rawClip[key];
                if (
                    !Array.isArray(children) ||
                    children.some(
                        (child) => !hasCanonicalStoredId(child, seenIds),
                    )
                ) {
                    return true;
                }
            }
            if (
                rawClip.retake !== null &&
                !hasCanonicalStoredId(rawClip.retake, seenIds)
            ) {
                return true;
            }
        }
        for (const rawTrack of parsed.audioTracks) {
            if (
                !hasCanonicalStoredId(rawTrack, seenIds) ||
                !Array.isArray(rawTrack.spans) ||
                rawTrack.spans.some(
                    (span) => !hasCanonicalStoredId(span, seenIds),
                )
            ) {
                return true;
            }
        }
        return false;
    } catch {
        return true;
    }
};

const saveRequestedState = (
    requestedInput: VideoStagesConfig,
    options: SaveStateOptions | undefined,
    snapshot = store.getSnapshot(),
): void => {
    // Both helpers below upgrade in place. Run them only on private clones so
    // compatibility callers keep ownership of every object they supplied.
    const requested = structuredClone(requestedInput);
    ensureAuthoringDocumentIdentity(requested);
    assignMissingHues(requested.clips);

    const before = structuredClone(snapshot.state);
    ensureAuthoringDocumentIdentity(before);
    assignMissingHues(before.clips);

    const diffCommand = (() => {
        try {
            return diffDocuments(before, requested);
        } catch (error) {
            return throwSaveFailure("diff", error);
        }
    })();
    // A parsed legacy carrier can already equal the requested canonical model,
    // leaving the semantic diff empty even though schema/IDs still need their
    // one-time durable migration. Represent that compatibility write as a
    // named root command rather than bypassing command dispatch.
    const command =
        diffCommand.commands.length === 0 &&
        dataCarrierNeedsCanonicalMigration()
            ? {
                  type: "batch" as const,
                  commands: [
                      {
                          type: "root.patch" as const,
                          patch: { schemaVersion: requested.schemaVersion },
                      },
                  ],
              }
            : diffCommand;

    const willNotifyDom = options?.notifyDomChange !== false;
    const result = store.dispatch(
        command,
        options?.origin ?? "timeline",
        willNotifyDom,
        options?.expectedRevision ?? snapshot.revision,
        options?.valueOnly ? "value-only" : undefined,
    );
    if (!result.applied) {
        throwSaveFailure("dispatch", result.failure ?? "unknown failure");
    }

    videoStagesDebugLog("persistence", "saveState", {
        notifyDomChange: options?.notifyDomChange,
        willNotifyDom,
        commandCount: command.commands.length,
        revision: result.revision,
        impacts: result.impacts,
    });
};

export const saveState = (
    state: VideoStagesConfig,
    options?: SaveStateOptions,
): void => saveRequestedState(state, options);

/**
 * Repository facade for atomic stable-ID document edits.
 *
 * Existing saveState/saveClips remain available while callers migrate away
 * from whole-document mutation.
 */
export const dispatchDocumentCommand = (
    command: DocumentCommand,
    options?: DispatchDocumentCommandOptions,
): TimelineDispatchResult => {
    const willNotifyDom = options?.notifyDomChange !== false;
    const result = store.dispatch(
        command,
        options?.origin ?? "timeline",
        willNotifyDom,
        options?.expectedRevision,
        options?.valueOnly ? "value-only" : undefined,
    );
    videoStagesDebugLog("persistence", "dispatchDocumentCommand", {
        command: command.type,
        applied: result.applied,
        failure: result.failure,
        revision: result.revision,
        impacts: result.impacts,
        willNotifyDom,
    });
    return result;
};

export const getClips = (): Clip[] => getState().clips;

export const saveClips = (clips: Clip[], options?: SaveStateOptions): void => {
    videoStagesDebugLog("persistence", "saveClips", {
        clipCount: clips.length,
    });
    const snapshot = store.getSnapshot();
    const state = structuredClone(snapshot.state);
    state.clips = structuredClone(clips);
    const notifyDomChange =
        options?.notifyDomChange !== undefined
            ? options.notifyDomChange
            : isVideoStagesEnabled();
    saveRequestedState(state, { ...options, notifyDomChange }, snapshot);
};
