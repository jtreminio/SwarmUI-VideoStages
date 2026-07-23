import { ROOT_DIMENSION_MIN, ROOT_FPS_MIN } from "../constants";
import {
    ensureAuthoringDocumentIdentity,
    ensureClipEntityIdentities,
} from "../identity";
import { normalizeAudioTracks, normalizeClip } from "../normalization";
import { getDefaultStageModel, getRootDefaults } from "../rootDefaults";
import type { StoredClip } from "../storageTypes";
import {
    type CanonicalClip,
    type Clip,
    CURRENT_AUTHORING_SCHEMA_VERSION,
    type VideoStagesConfig,
} from "../types";
import { isRecord, toNumber } from "../utils";

export type InheritedDims = Pick<VideoStagesConfig, "width" | "height" | "fps">;
export type RootDims = Pick<
    VideoStagesConfig,
    "width" | "height" | "fps" | "dimsExplicit" | "fpsExplicit"
>;

export interface DecodedStoredDocument {
    dims: RootDims;
    clips: Clip[];
    audioTracks: VideoStagesConfig["audioTracks"];
}

const toIntOrNull = (value: unknown): number | null => {
    if (value == null || value === "") return null;
    const num = toNumber(`${value}`, Number.NaN);
    return Number.isFinite(num) ? Math.round(num) : null;
};

export const resolveRootDims = (
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
        width: dimsExplicit ? width : inherited.width,
        height: dimsExplicit ? height : inherited.height,
        fps: fpsExplicit ? fps : inherited.fps,
        dimsExplicit,
        fpsExplicit,
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
            audioSegments: clip.audioSegments.map((segment) => ({
                id: segment.id,
                source: segment.source,
                startSeconds: segment.startSeconds,
                trimStartSeconds: segment.trimStartSeconds,
                lengthSeconds: segment.lengthSeconds,
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

export const serializeStateForStorage = (state: VideoStagesConfig): string => {
    ensureAuthoringDocumentIdentity(state);
    const out: Record<string, unknown> = {
        schemaVersion: CURRENT_AUTHORING_SCHEMA_VERSION,
    };
    if (state.dimsExplicit) {
        out.width = Math.round(state.width);
        out.height = Math.round(state.height);
    }
    if (state.fpsExplicit) out.fps = Math.round(state.fps);
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
 * v3 keeps scalar normalization deliberately forgiving, but its collection
 * topology is strict: a malformed present collection must never disappear as
 * an empty list, and a malformed item must never turn into a default entity.
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
            !hasArrayOfRecords(clip, "audioSegments") ||
            !hasArrayOfRecords(clip, "icLoras")
        ) {
            return false;
        }
        const stages = Array.isArray(clip.stages) ? clip.stages : [];
        for (const stage of stages) {
            if (
                !hasArrayOfRecords(stage, "loras") ||
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

/** Strict v3 decode. Older or malformed roots are rejected, not migrated. */
export const decodeStoredDocument = (
    serialized: string,
    inherited: InheritedDims,
): DecodedStoredDocument | null => {
    try {
        const parsed: unknown = JSON.parse(serialized);
        if (
            !isRecord(parsed) ||
            parsed.schemaVersion !== CURRENT_AUTHORING_SCHEMA_VERSION ||
            !hasValidStoredCollections(parsed)
        ) {
            return null;
        }
        const dims = resolveRootDims(inherited, {
            width: parsed.width,
            height: parsed.height,
            fps: parsed.fps,
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
