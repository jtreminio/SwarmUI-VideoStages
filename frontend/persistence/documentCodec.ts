import { ROOT_DIMENSION_MIN } from "../constants";
import { getVideoStagesHostBridge } from "../host";
import {
    ensureAuthoringDocumentIdentity,
    ensureClipEntityIdentities,
} from "../identity";
import { normalizeAudioTracks, normalizeClip } from "../normalization";
import { optionalNonNegativeNumber } from "../normalizationShared";
import { getDefaultStageModel, getRootDefaults } from "../rootDefaults";
import type { StoredClip } from "../storageTypes";
import {
    type CanonicalClip,
    type CanonicalVideoStagesConfig,
    type Clip,
    CURRENT_AUTHORING_SCHEMA_VERSION,
    type VideoStagesConfig,
} from "../types";
import { isRecord } from "../utils";

export type InheritedDims = Pick<VideoStagesConfig, "width" | "height" | "fps">;
export type RootDims = Pick<
    VideoStagesConfig,
    "width" | "height" | "fps" | "dimsExplicit"
>;

export interface DecodedStoredDocument {
    dims: RootDims;
    clips: Clip[];
    audioTracks: VideoStagesConfig["audioTracks"];
}

const toIntOrNull = (value: unknown): number | null => {
    const num = optionalNonNegativeNumber(value);
    return num === null ? null : Math.round(num);
};

export const resolveRootDims = (
    inherited: InheritedDims,
    stored: { width?: unknown; height?: unknown },
): RootDims => {
    const width = toIntOrNull(stored.width);
    const height = toIntOrNull(stored.height);
    const dimsExplicit =
        width !== null &&
        width >= ROOT_DIMENSION_MIN &&
        height !== null &&
        height >= ROOT_DIMENSION_MIN;
    return {
        width: dimsExplicit ? width : inherited.width,
        height: dimsExplicit ? height : inherited.height,
        // fps is never stored: the timeline always follows the core Video FPS
        // param (the backend falls back to it too when the JSON has no fps).
        fps: inherited.fps,
        dimsExplicit,
    };
};

export const createRootConfig = (
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
            architecture: clip.architecture,
            modelProfileId: clip.modelProfileId,
            skipped: clip.skipped,
            boundaryOut: clip.boundaryOut,
            boundaryOutCarryAudio: clip.boundaryOutCarryAudio,
            boundaryOutOverlap: clip.boundaryOutOverlap,
            duration: clip.duration,
            audioSource: clip.audioSource,
            icLoras: clip.icLoras.map((entry) => ({
                lora: entry.lora,
                preset: entry.preset,
                driveSource: entry.driveSource,
                driveData: entry.driveData,
                driveMediaKinds: entry.driveMediaKinds,
                stage: entry.stage,
                strength: entry.strength,
                attentionStrength: entry.attentionStrength,
                controlType: entry.controlType,
                driveMedia: entry.driveMedia,
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
                icLoraStrengths: stage.icLoraStrengths,
                refStrengths: stage.refStrengths,
                upscale: stage.upscale,
                upscaleMethod: stage.upscaleMethod,
                model: stage.model,
                modelProfileId: stage.modelProfileId,
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

const timelinePointProjection = (
    clips: CanonicalClip[],
    seconds: number,
    edge: "start" | "end",
): { clipId: string; offsetSeconds: number } | null => {
    if (!Number.isFinite(seconds) || seconds < 0 || clips.length === 0) {
        return null;
    }
    let cursor = 0;
    for (let index = 0; index < clips.length; index++) {
        const clip = clips[index];
        const duration = Math.max(0, clip.duration || 0);
        const clipEnd = cursor + duration;
        const isLast = index === clips.length - 1;
        const ownsPoint =
            edge === "start"
                ? seconds < clipEnd || isLast
                : seconds <= clipEnd || isLast;
        if (ownsPoint) {
            return {
                clipId: clip.id,
                offsetSeconds: Math.max(
                    0,
                    Math.min(duration, seconds - cursor),
                ),
            };
        }
        cursor = clipEnd;
    }
    return null;
};

/**
 * Absolute authoring seconds position the block in the browser. These derived
 * clip anchors let the backend preserve the exact visible seam when compiled
 * LTX frame alignment makes a clip's runtime duration differ by one frame.
 */
const timelineSpanProjection = (
    clips: CanonicalClip[],
    span: CanonicalVideoStagesConfig["audioTracks"][number]["spans"][number],
): Record<string, unknown> | null => {
    if (
        span.timelineStartSeconds === null ||
        span.timelineLengthSeconds === null
    ) {
        return null;
    }
    const start = timelinePointProjection(
        clips,
        span.timelineStartSeconds,
        "start",
    );
    const end = timelinePointProjection(
        clips,
        span.timelineStartSeconds + span.timelineLengthSeconds,
        "end",
    );
    return start && end
        ? {
              firstClipId: start.clipId,
              lastClipId: end.clipId,
              clipStartOffsetSeconds: start.offsetSeconds,
              clipEndOffsetSeconds: end.offsetSeconds,
          }
        : null;
};

export const serializeStateForStorage = (state: VideoStagesConfig): string => {
    ensureAuthoringDocumentIdentity(state);
    const canonical = state as CanonicalVideoStagesConfig;
    const out: Record<string, unknown> = {
        schemaVersion: CURRENT_AUTHORING_SCHEMA_VERSION,
    };
    if (state.dimsExplicit) {
        out.width = Math.round(state.width);
        out.height = Math.round(state.height);
    }
    out.clips = serializeClipsForStorage(state.clips);
    out.audioTracks = canonical.audioTracks.map((track) => ({
        id: track.id,
        ...(track.volume === undefined ? {} : { volume: track.volume }),
        source: {
            kind: track.source.kind,
            reference: track.source.reference,
            uploadedAudio: track.source.uploadedAudio,
        },
        spans: track.spans.map((span) => ({
            id: span.id,
            timelineStartSeconds: span.timelineStartSeconds,
            timelineLengthSeconds: span.timelineLengthSeconds,
            sourceStartSeconds: span.sourceStartSeconds,
            projection: timelineSpanProjection(canonical.clips, span),
        })),
    }));
    return JSON.stringify(out);
};

const isTransientBrowserMedia = (
    media: { data: string } | null | undefined,
): boolean => {
    const data = media?.data.trim().toLowerCase() ?? "";
    return data.startsWith("data:") || data.startsWith("blob:");
};

/**
 * Durable browser storage deliberately excludes embedded upload payloads.
 * The normal Data carrier still receives the complete runtime document so a
 * generation can use newly selected media until the page is reloaded.
 */
export const serializeStateForDurableStorage = (
    state: VideoStagesConfig,
): string => {
    ensureAuthoringDocumentIdentity(state);
    const durable = structuredClone(state);
    for (const clip of durable.clips) {
        if (isTransientBrowserMedia(clip.uploadedAudio)) {
            clip.uploadedAudio = null;
        }
        if (
            clip.sourceVideo &&
            isTransientBrowserMedia({ data: clip.sourceVideo.data })
        ) {
            clip.sourceVideo = null;
        }
        for (const ref of clip.refs) {
            if (isTransientBrowserMedia(ref.uploadedImage)) {
                ref.uploadedImage = null;
            }
        }
        for (const icLora of clip.icLoras) {
            if (isTransientBrowserMedia(icLora.driveMedia)) {
                icLora.driveMedia = null;
            }
        }
    }
    for (const track of durable.audioTracks ?? []) {
        if (isTransientBrowserMedia(track.source.uploadedAudio)) {
            track.source.uploadedAudio = null;
        }
    }
    return serializeStateForStorage(durable);
};

const hasArrayOfRecords = (
    owner: Record<string, unknown>,
    key: string,
): boolean => {
    if (!Object.hasOwn(owner, key)) {
        return true;
    }
    const value = owner[key];
    return Array.isArray(value) && value.every(isRecord);
};

/**
 * Scalar normalization is deliberately forgiving, but collection topology is
 * strict: a malformed present collection must never disappear as an empty
 * list, and a malformed item must never turn into a default entity.
 */
const hasValidStoredCollections = (
    parsed: Record<string, unknown>,
): parsed is Record<string, unknown> & {
    clips: Record<string, unknown>[];
} => {
    if (!Array.isArray(parsed.clips) || !parsed.clips.every(isRecord)) {
        return false;
    }
    if (
        !hasArrayOfRecords(parsed, "audioTracks") ||
        !hasArrayOfRecords(parsed, "clips")
    ) {
        return false;
    }
    for (const clip of parsed.clips) {
        if (
            !hasArrayOfRecords(clip, "stages") ||
            !hasArrayOfRecords(clip, "refs") ||
            !hasArrayOfRecords(clip, "icLoras")
        ) {
            return false;
        }
        const stages = Array.isArray(clip.stages) ? clip.stages : [];
        for (const stage of stages) {
            if (
                !hasArrayOfRecords(stage, "loras") ||
                (Object.hasOwn(stage, "icLoraStrengths") &&
                    (!Array.isArray(stage.icLoraStrengths) ||
                        !stage.icLoraStrengths.every(
                            (strength: unknown) =>
                                typeof strength === "number" &&
                                Number.isFinite(strength),
                        ))) ||
                (Object.hasOwn(stage, "refStrengths") &&
                    (!Array.isArray(stage.refStrengths) ||
                        !stage.refStrengths.every(
                            (strength: unknown) =>
                                typeof strength === "number" &&
                                Number.isFinite(strength),
                        )))
            ) {
                return false;
            }
        }
    }
    const tracks = Array.isArray(parsed.audioTracks) ? parsed.audioTracks : [];
    return tracks.every(
        (track) =>
            hasArrayOfRecords(track, "spans") &&
            (!Object.hasOwn(track, "source") || isRecord(track.source)),
    );
};

const OUTDATED_SCHEMA_NOTICE =
    "VideoStages: the saved timeline was created by an older version and " +
    "could not be loaded.";

// Repeated decodes of the same stale carrier value must not stack notices.
let noticedOutdatedDocument: string | null = null;

const noticeOutdatedSchema = (serialized: string): void => {
    if (noticedOutdatedDocument === serialized) {
        return;
    }
    noticedOutdatedDocument = serialized;
    getVideoStagesHostBridge().showError(OUTDATED_SCHEMA_NOTICE);
};

/** Strict current decode: any other schema version is rejected outright. */
export const decodeStoredDocument = (
    serialized: string,
    inherited: InheritedDims,
): DecodedStoredDocument | null => {
    try {
        const parsed: unknown = JSON.parse(serialized);
        if (!isRecord(parsed)) {
            return null;
        }
        if (parsed.schemaVersion !== CURRENT_AUTHORING_SCHEMA_VERSION) {
            noticeOutdatedSchema(serialized);
            return null;
        }
        if (!hasValidStoredCollections(parsed)) {
            return null;
        }
        const dims = resolveRootDims(inherited, {
            width: parsed.width,
            height: parsed.height,
        });
        return {
            dims,
            clips: parsed.clips.map((entry) =>
                normalizeClip(
                    entry,
                    getRootDefaults,
                    getDefaultStageModel,
                    dims.fps,
                ),
            ),
            audioTracks: normalizeAudioTracks(parsed.audioTracks),
        };
    } catch {
        return null;
    }
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

export const storedDocumentNeedsCanonicalIdRepair = (
    serialized: string,
): boolean => {
    try {
        const parsed: unknown = JSON.parse(serialized);
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
            for (const key of ["stages", "refs"] as const) {
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
