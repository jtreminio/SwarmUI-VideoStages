import { sealSkipSuffix } from "../clipSemantics";
import { ROOT_DIMENSION_MIN } from "../constants";
import {
    MEDIA_SOURCE_PREVIOUS_CLIP,
    MEDIA_SOURCE_UPLOAD,
} from "../generatedMediaSource";
import { getVideoStagesHostBridge } from "../host";
import {
    ensureAuthoringDocumentIdentity,
    ensureClipEntityIdentities,
} from "../identity";
import { normalizeAudioTracks } from "../normalizationAudio";
import { normalizeClip } from "../normalizationClip";
import { optionalNonNegativeNumber } from "../normalizationShared";
import type { StoredClip } from "../storageTypes";
import {
    type AuthoringDocument,
    type CanonicalAuthoringDocument,
    type CanonicalClip,
    type Clip,
    CURRENT_AUTHORING_SCHEMA_VERSION,
    type RootDefaults,
} from "../types";
import { isRecord } from "../utils";

export type InheritedDims = Pick<AuthoringDocument, "width" | "height" | "fps">;
export type RootDims = Pick<
    AuthoringDocument,
    "width" | "height" | "fps" | "dimsExplicit"
>;

export interface DecodedStoredDocument {
    dims: RootDims;
    clips: Clip[];
    audioTracks: AuthoringDocument["audioTracks"];
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
        // The timeline follows core's Video FPS param.
        fps: inherited.fps,
        dimsExplicit,
    };
};

export const createRootConfig = (
    dims: RootDims,
    clips: Clip[],
    audioTracks: AuthoringDocument["audioTracks"] = [],
): AuthoringDocument => {
    const config: AuthoringDocument = {
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
            architectureHint: clip.architectureHint,
            modelProfileId: clip.modelProfileId,
            skipped: clip.skipped,
            boundaryOut: clip.boundaryOut,
            boundaryOutCarryAudio: clip.boundaryOutCarryAudio,
            boundaryOutReferenceScale: clip.boundaryOutReferenceScale,
            boundaryOutReferenceIncludeSoundtrack:
                clip.boundaryOutReferenceIncludeSoundtrack,
            boundaryOutOverlap: clip.boundaryOutOverlap,
            duration: clip.duration,
            useSeedVr: clip.useSeedVr,
            refFraming: clip.refFraming,
            h3AttentionWindowSeconds: clip.h3AttentionWindowSeconds,
            h3TextEncoder: clip.h3TextEncoder,
            audioSource: clip.audioSource,
            loras: clip.loras.map((entry) => ({
                name: entry.name,
            })),
            icLoras: clip.icLoras.map((entry) => ({
                id: entry.id,
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
            uploadedAudioDurationSeconds: clip.uploadedAudioDurationSeconds,
            uploadedAudioStartSeconds: clip.uploadedAudioStartSeconds,
            uploadedAudioLengthSeconds: clip.uploadedAudioLengthSeconds,
            initVideo: clip.initVideo
                ? {
                      source: clip.initVideo.source ?? MEDIA_SOURCE_UPLOAD,
                      data: clip.initVideo.data,
                      fileName: clip.initVideo.fileName,
                      fps: clip.initVideo.fps,
                      durationSeconds: clip.initVideo.durationSeconds,
                      startSeconds: clip.initVideo.startSeconds,
                      lengthSeconds: clip.initVideo.lengthSeconds,
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
            references: clip.references.map((reference) => ({
                id: reference.id,
                kind: reference.kind,
                source: reference.source,
                uploadedMedia: reference.uploadedMedia,
                includeSoundtrack: reference.includeSoundtrack,
                mediaDurationSeconds: reference.mediaDurationSeconds,
                drivesClipLength: reference.drivesClipLength,
                mediaScale: reference.mediaScale,
                startSeconds: reference.startSeconds,
                lengthSeconds: reference.lengthSeconds,
            })),
            keyframes: clip.frameRefs.map((ref) => ({
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
                loraWeights: stage.loraWeights,
                keyframeStrengths: stage.frameRefStrengths,
                upscale: stage.upscale,
                upscaleMethod: stage.upscaleMethod,
                model: stage.model,
                modelProfileId: stage.modelProfileId,
                steps: stage.steps,
                cfgScale: stage.cfgScale,
                sampler: stage.sampler,
                scheduler: stage.scheduler,
            })),
        }),
    );
};

interface ProjectionClip {
    id: string;
    duration: number;
}

interface SpanProjection {
    firstClipId: string;
    lastClipId: string;
    clipStartOffsetSeconds: number;
    clipEndOffsetSeconds: number;
}

const timelinePointProjection = (
    clips: readonly ProjectionClip[],
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

/** Stores seam anchors across backend frame alignment. */
const timelineSpanProjection = (
    clips: readonly ProjectionClip[],
    span: Pick<
        CanonicalAuthoringDocument["audioTracks"][number]["spans"][number],
        "timelineStartSeconds" | "timelineLengthSeconds"
    >,
): SpanProjection | null => {
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

export const serializeStateForStorage = (state: AuthoringDocument): string => {
    ensureAuthoringDocumentIdentity(state);
    const canonical = state as CanonicalAuthoringDocument;
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
            mediaDurationSeconds: track.source.mediaDurationSeconds,
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

/** Durable storage excludes transient browser media payloads. */
export const serializeStateForDurableStorage = (
    state: AuthoringDocument,
): string => {
    ensureAuthoringDocumentIdentity(state);
    const durable = structuredClone(state);
    for (const clip of durable.clips) {
        if (isTransientBrowserMedia(clip.uploadedAudio)) {
            clip.uploadedAudio = null;
            clip.uploadedAudioDurationSeconds = 0;
            clip.uploadedAudioStartSeconds = 0;
            clip.uploadedAudioLengthSeconds = 0;
        }
        if (
            clip.initVideo &&
            clip.initVideo.source !== MEDIA_SOURCE_PREVIOUS_CLIP &&
            isTransientBrowserMedia({ data: clip.initVideo.data })
        ) {
            clip.initVideo = null;
        }
        for (const ref of clip.frameRefs) {
            if (isTransientBrowserMedia(ref.uploadedImage)) {
                ref.uploadedImage = null;
            }
        }
        for (const reference of clip.references) {
            if (isTransientBrowserMedia(reference.uploadedMedia)) {
                reference.uploadedMedia = null;
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

/** Rejects malformed collections instead of normalizing them away. */
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
            !hasArrayOfRecords(clip, "keyframes") ||
            !hasArrayOfRecords(clip, "references") ||
            !hasArrayOfRecords(clip, "icLoras") ||
            (Object.hasOwn(clip, "loras") && !hasArrayOfRecords(clip, "loras"))
        ) {
            return false;
        }
        const stages = Array.isArray(clip.stages) ? clip.stages : [];
        for (const stage of stages) {
            if (
                (Object.hasOwn(stage, "loras") &&
                    !hasArrayOfRecords(stage, "loras")) ||
                (Object.hasOwn(stage, "loraWeights") &&
                    (!Array.isArray(stage.loraWeights) ||
                        !stage.loraWeights.every(
                            (weight: unknown) =>
                                typeof weight === "number" &&
                                Number.isFinite(weight),
                        ))) ||
                (Object.hasOwn(stage, "icLoraStrengths") &&
                    (!Array.isArray(stage.icLoraStrengths) ||
                        !stage.icLoraStrengths.every(
                            (strength: unknown) =>
                                typeof strength === "number" &&
                                Number.isFinite(strength),
                        ))) ||
                (Object.hasOwn(stage, "keyframeStrengths") &&
                    (!Array.isArray(stage.keyframeStrengths) ||
                        !stage.keyframeStrengths.every(
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

const DIVERGENT_PROJECTION_NOTICE =
    "VideoStages: the saved timeline has audio spans whose clip anchors " +
    "disagree with their timeline seconds. The seconds were used and the " +
    "anchors will be rewritten on the next save — re-check those spans.";

let noticedDivergentProjection: string | null = null;

const SPAN_PROJECTION_TOLERANCE = 1e-6;

const numberAt = (
    owner: Record<string, unknown>,
    key: string,
): number | null =>
    typeof owner[key] === "number" && Number.isFinite(owner[key])
        ? (owner[key] as number)
        : null;

const storedSpanProjection = (
    span: Record<string, unknown>,
): SpanProjection | null => {
    const raw = span.projection;
    if (!isRecord(raw)) {
        return null;
    }
    const first = raw.firstClipId;
    const last = raw.lastClipId;
    const startOffset = numberAt(raw, "clipStartOffsetSeconds");
    const endOffset = numberAt(raw, "clipEndOffsetSeconds");
    return typeof first === "string" &&
        typeof last === "string" &&
        startOffset !== null &&
        endOffset !== null
        ? {
              firstClipId: first,
              lastClipId: last,
              clipStartOffsetSeconds: startOffset,
              clipEndOffsetSeconds: endOffset,
          }
        : null;
};

/** Reports stored seam anchors that disagree with their authored seconds. */
const hasDivergentSpanProjection = (
    parsed: Record<string, unknown> & { clips: Record<string, unknown>[] },
): boolean => {
    const clips: ProjectionClip[] = parsed.clips.map((clip) => ({
        id: typeof clip.id === "string" ? clip.id : "",
        duration: numberAt(clip, "duration") ?? 0,
    }));
    const tracks = Array.isArray(parsed.audioTracks) ? parsed.audioTracks : [];
    for (const track of tracks) {
        const spans =
            isRecord(track) && Array.isArray(track.spans) ? track.spans : [];
        for (const span of spans) {
            if (!isRecord(span)) {
                continue;
            }
            const stored = storedSpanProjection(span);
            if (!stored) {
                continue;
            }
            const expected = timelineSpanProjection(clips, {
                timelineStartSeconds: numberAt(span, "timelineStartSeconds"),
                timelineLengthSeconds: numberAt(span, "timelineLengthSeconds"),
            });
            if (
                !expected ||
                expected.firstClipId !== stored.firstClipId ||
                expected.lastClipId !== stored.lastClipId ||
                Math.abs(
                    expected.clipStartOffsetSeconds -
                        stored.clipStartOffsetSeconds,
                ) > SPAN_PROJECTION_TOLERANCE ||
                Math.abs(
                    expected.clipEndOffsetSeconds - stored.clipEndOffsetSeconds,
                ) > SPAN_PROJECTION_TOLERANCE
            ) {
                return true;
            }
        }
    }
    return false;
};

const noticeDivergentProjection = (serialized: string): void => {
    if (noticedDivergentProjection === serialized) {
        return;
    }
    noticedDivergentProjection = serialized;
    getVideoStagesHostBridge().showError(DIVERGENT_PROJECTION_NOTICE);
};

const FRAME_REFS_SCHEMA_VERSION = 7;

const renameKey = (
    target: Record<string, unknown>,
    oldKey: string,
    newKey: string,
): void => {
    if (!(oldKey in target)) {
        return;
    }
    if (!(newKey in target)) {
        target[newKey] = target[oldKey];
    }
    delete target[oldKey];
};

/** Renames the v7 frame-reference fields to the product vocabulary. */
const migrateStoredDocument = (
    parsed: Record<string, unknown>,
): Record<string, unknown> | null => {
    if (parsed.schemaVersion === CURRENT_AUTHORING_SCHEMA_VERSION) {
        return parsed;
    }
    if (
        parsed.schemaVersion !== FRAME_REFS_SCHEMA_VERSION ||
        !Array.isArray(parsed.clips)
    ) {
        return null;
    }
    const migrated = structuredClone(parsed);
    migrated.schemaVersion = CURRENT_AUTHORING_SCHEMA_VERSION;
    for (const rawClip of migrated.clips as unknown[]) {
        if (!isRecord(rawClip)) {
            continue;
        }
        renameKey(rawClip, "frameRefs", "keyframes");
        if (!Array.isArray(rawClip.stages)) {
            continue;
        }
        for (const rawStage of rawClip.stages) {
            if (isRecord(rawStage)) {
                renameKey(rawStage, "frameRefStrengths", "keyframeStrengths");
            }
        }
    }
    return migrated;
};

export const decodeStoredDocument = (
    serialized: string,
    inherited: InheritedDims,
    defaults: RootDefaults,
    defaultStageModel: string,
): DecodedStoredDocument | null => {
    try {
        const parsed: unknown = JSON.parse(serialized);
        if (!isRecord(parsed)) {
            return null;
        }
        const current = migrateStoredDocument(parsed);
        if (!current) {
            noticeOutdatedSchema(serialized);
            return null;
        }
        if (!hasValidStoredCollections(current)) {
            return null;
        }
        if (hasDivergentSpanProjection(current)) {
            noticeDivergentProjection(serialized);
        }
        const dims = resolveRootDims(inherited, {
            width: current.width,
            height: current.height,
        });
        const clips = current.clips.map((entry) =>
            normalizeClip(entry, defaults, defaultStageModel, dims.fps),
        );
        sealSkipSuffix(clips);
        return {
            dims,
            clips,
            audioTracks: normalizeAudioTracks(current.audioTracks),
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
        seen.has(value.id) ||
        value.id.includes("_legacy_")
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
            for (const key of ["stages", "keyframes", "references"] as const) {
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
