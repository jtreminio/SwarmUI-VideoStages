import { collectAuthoringEntityIds } from "./identity";
import type {
    CanonicalAudioSegment,
    CanonicalAudioTrack,
    CanonicalAudioTrackSpan,
    CanonicalClip,
    CanonicalPromptWindow,
    CanonicalRefImage,
    CanonicalRetake,
    CanonicalStage,
    CanonicalVideoStagesConfig,
} from "./types";

export type ChangeImpact = "value" | "structure" | "selection" | "capabilities";

export type CommandFailure =
    | "missing-target"
    | "duplicate-id"
    | "invalid-id"
    | "retake-already-exists";

export interface DocumentCommandResult {
    document: CanonicalVideoStagesConfig;
    applied: boolean;
    impacts: readonly ChangeImpact[];
    failure?: CommandFailure;
}

type RootSettingsPatch = Partial<
    Pick<
        CanonicalVideoStagesConfig,
        | "schemaVersion"
        | "width"
        | "height"
        | "fps"
        | "dimsExplicit"
        | "fpsExplicit"
    >
>;

type ClipPatch = Partial<
    Omit<
        CanonicalClip,
        "id" | "audioSegments" | "promptWindows" | "retake" | "refs" | "stages"
    >
>;
type StagePatch = Partial<Omit<CanonicalStage, "id">>;
type RefPatch = Partial<Omit<CanonicalRefImage, "id">>;
type AudioSegmentPatch = Partial<Omit<CanonicalAudioSegment, "id">>;
type PromptWindowPatch = Partial<Omit<CanonicalPromptWindow, "id">>;
type RetakePatch = Partial<Omit<CanonicalRetake, "id">>;
type AudioTrackPatch = Partial<Omit<CanonicalAudioTrack, "id" | "spans">>;
type AudioSpanPatch = Partial<Omit<CanonicalAudioTrackSpan, "id">>;

export type DocumentCommand =
    | { type: "batch"; commands: readonly DocumentCommand[] }
    | { type: "root.patch"; patch: RootSettingsPatch }
    | {
          type: "clip.add";
          clip: CanonicalClip;
          beforeClipId?: string | null;
      }
    | { type: "clip.remove"; clipId: string }
    | {
          type: "clip.move";
          clipId: string;
          beforeClipId: string | null;
      }
    | { type: "clip.patch"; clipId: string; patch: ClipPatch }
    | {
          type: "stage.add";
          clipId: string;
          stage: CanonicalStage;
          beforeStageId?: string | null;
      }
    | { type: "stage.remove"; clipId: string; stageId: string }
    | {
          type: "stage.move";
          clipId: string;
          stageId: string;
          beforeStageId: string | null;
      }
    | {
          type: "stage.patch";
          clipId: string;
          stageId: string;
          patch: StagePatch;
      }
    | {
          type: "ref.add";
          clipId: string;
          ref: CanonicalRefImage;
          beforeRefId?: string | null;
      }
    | { type: "ref.remove"; clipId: string; refId: string }
    | {
          type: "ref.move";
          clipId: string;
          refId: string;
          beforeRefId: string | null;
      }
    | {
          type: "ref.patch";
          clipId: string;
          refId: string;
          patch: RefPatch;
      }
    | {
          type: "audio-segment.add";
          clipId: string;
          segment: CanonicalAudioSegment;
          beforeSegmentId?: string | null;
      }
    | {
          type: "audio-segment.remove";
          clipId: string;
          segmentId: string;
      }
    | {
          type: "audio-segment.move";
          clipId: string;
          segmentId: string;
          beforeSegmentId: string | null;
      }
    | {
          type: "audio-segment.patch";
          clipId: string;
          segmentId: string;
          patch: AudioSegmentPatch;
      }
    | {
          type: "prompt-window.add";
          clipId: string;
          window: CanonicalPromptWindow;
          beforeWindowId?: string | null;
      }
    | {
          type: "prompt-window.remove";
          clipId: string;
          windowId: string;
      }
    | {
          type: "prompt-window.move";
          clipId: string;
          windowId: string;
          beforeWindowId: string | null;
      }
    | {
          type: "prompt-window.patch";
          clipId: string;
          windowId: string;
          patch: PromptWindowPatch;
      }
    | { type: "retake.add"; clipId: string; retake: CanonicalRetake }
    | {
          type: "retake.remove";
          clipId: string;
          retakeId: string;
      }
    | {
          type: "retake.patch";
          clipId: string;
          retakeId: string;
          patch: RetakePatch;
      }
    | {
          type: "audio-track.add";
          track: CanonicalAudioTrack;
          beforeTrackId?: string | null;
      }
    | { type: "audio-track.remove"; trackId: string }
    | {
          type: "audio-track.move";
          trackId: string;
          beforeTrackId: string | null;
      }
    | {
          type: "audio-track.patch";
          trackId: string;
          patch: AudioTrackPatch;
      }
    | {
          type: "audio-span.add";
          trackId: string;
          span: CanonicalAudioTrackSpan;
          beforeSpanId?: string | null;
      }
    | {
          type: "audio-span.remove";
          trackId: string;
          spanId: string;
      }
    | {
          type: "audio-span.move";
          trackId: string;
          spanId: string;
          beforeSpanId: string | null;
      }
    | {
          type: "audio-span.patch";
          trackId: string;
          spanId: string;
          patch: AudioSpanPatch;
      };

const VALUE: readonly ChangeImpact[] = ["value"];
const VALUE_CAPABILITIES: readonly ChangeImpact[] = ["value", "capabilities"];
const STRUCTURE: readonly ChangeImpact[] = ["structure", "capabilities"];
const REMOVE_STRUCTURE: readonly ChangeImpact[] = [
    "structure",
    "selection",
    "capabilities",
];
const MOVE_STRUCTURE: readonly ChangeImpact[] = ["structure"];
const IMPACT_ORDER: readonly ChangeImpact[] = [
    "value",
    "structure",
    "selection",
    "capabilities",
];

const clone = <T>(value: T): T => structuredClone(value);

const normalizedId = (value: unknown): string | null => {
    if (typeof value !== "string") {
        return null;
    }
    const id = value.trim();
    return id.length > 0 ? id : null;
};

const findClip = (
    document: CanonicalVideoStagesConfig,
    clipId: string,
): CanonicalClip | null =>
    document.clips.find((clip) => clip.id === clipId) ?? null;

const findTrack = (
    document: CanonicalVideoStagesConfig,
    trackId: string,
): CanonicalAudioTrack | null =>
    document.audioTracks.find((track) => track.id === trackId) ?? null;

const candidateIds = (
    entity:
        | CanonicalClip
        | CanonicalStage
        | CanonicalRefImage
        | CanonicalAudioSegment
        | CanonicalPromptWindow
        | CanonicalRetake
        | CanonicalAudioTrack
        | CanonicalAudioTrackSpan,
): string[] => {
    if ("stages" in entity && "refs" in entity) {
        return [
            entity.id,
            ...entity.stages.map((stage) => stage.id),
            ...entity.refs.map((ref) => ref.id),
            ...entity.audioSegments.map((segment) => segment.id),
            ...entity.promptWindows.map((window) => window.id),
            ...(entity.retake ? [entity.retake.id] : []),
        ];
    }
    if ("spans" in entity) {
        return [entity.id, ...entity.spans.map((span) => span.id)];
    }
    return [entity.id];
};

const validateNewEntity = (
    document: CanonicalVideoStagesConfig,
    entity: Parameters<typeof candidateIds>[0],
): CommandFailure | null => {
    const ids = candidateIds(entity);
    if (ids.some((id) => normalizedId(id) !== id)) {
        return "invalid-id";
    }
    if (new Set(ids).size !== ids.length) {
        return "duplicate-id";
    }
    const existing = new Set(collectAuthoringEntityIds(document));
    return ids.some((id) => existing.has(id)) ? "duplicate-id" : null;
};

const addBefore = <T extends { id: string }>(
    items: T[],
    item: T,
    beforeId?: string | null,
): boolean => {
    if (beforeId == null) {
        items.push(item);
        return true;
    }
    const beforeIndex = items.findIndex(
        (candidate) => candidate.id === beforeId,
    );
    if (beforeIndex < 0) {
        return false;
    }
    items.splice(beforeIndex, 0, item);
    return true;
};

const removeById = <T extends { id: string }>(
    items: T[],
    id: string,
): boolean => {
    const index = items.findIndex((item) => item.id === id);
    if (index < 0) {
        return false;
    }
    items.splice(index, 1);
    return true;
};

const moveBefore = <T extends { id: string }>(
    items: T[],
    id: string,
    beforeId: string | null,
): boolean => {
    const fromIndex = items.findIndex((item) => item.id === id);
    if (fromIndex < 0) {
        return false;
    }
    if (beforeId !== null && !items.some((item) => item.id === beforeId)) {
        return false;
    }
    if (id === beforeId) {
        return true;
    }

    const [item] = items.splice(fromIndex, 1);
    if (beforeId === null) {
        items.push(item);
        return true;
    }
    const toIndex = items.findIndex((candidate) => candidate.id === beforeId);
    items.splice(toIndex, 0, item);
    return true;
};

const patchById = <T extends { id: string }>(
    items: T[],
    id: string,
    patch: Partial<Omit<T, "id">>,
): boolean => {
    const entity = items.find((item) => item.id === id);
    if (!entity) {
        return false;
    }
    Object.assign(entity, clone(patch), { id });
    return true;
};

const success = (
    document: CanonicalVideoStagesConfig,
    impacts: readonly ChangeImpact[],
): DocumentCommandResult => ({ document, applied: true, impacts });

const failure = (
    document: CanonicalVideoStagesConfig,
    reason: CommandFailure,
): DocumentCommandResult => ({
    document,
    applied: false,
    impacts: [],
    failure: reason,
});

const invalidNewEntity = (
    document: CanonicalVideoStagesConfig,
    entity: Parameters<typeof candidateIds>[0],
): DocumentCommandResult | null => {
    const reason = validateNewEntity(document, entity);
    return reason ? failure(document, reason) : null;
};

const assertNever = (command: never): never => {
    throw new Error(`Unhandled document command: ${JSON.stringify(command)}`);
};

const combineImpacts = (
    impacts: readonly (readonly ChangeImpact[])[],
): readonly ChangeImpact[] => {
    const included = new Set(impacts.flat());
    return IMPACT_ORDER.filter((impact) => included.has(impact));
};

/**
 * Applies one stable-ID command to an isolated authoring snapshot.
 *
 * The input and command payload are never mutated. Missing owners, targets, or
 * ID-relative insertion points fail closed and return an unchanged clone.
 */
export const reduceDocumentCommand = (
    source: CanonicalVideoStagesConfig,
    command: DocumentCommand,
): DocumentCommandResult => {
    const document = clone(source);

    switch (command.type) {
        case "batch": {
            let current = document;
            const impacts: (readonly ChangeImpact[])[] = [];
            for (const child of command.commands) {
                const result = reduceDocumentCommand(current, child);
                if (!result.applied) {
                    return failure(
                        clone(source),
                        result.failure as CommandFailure,
                    );
                }
                current = result.document;
                impacts.push(result.impacts);
            }
            return success(current, combineImpacts(impacts));
        }
        case "root.patch": {
            Object.assign(document, clone(command.patch));
            return success(document, VALUE_CAPABILITIES);
        }
        case "clip.add": {
            const invalid = invalidNewEntity(document, command.clip);
            if (invalid) return invalid;
            if (
                !addBefore(
                    document.clips,
                    clone(command.clip),
                    command.beforeClipId,
                )
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "clip.remove":
            return removeById(document.clips, command.clipId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        case "clip.move":
            return moveBefore(
                document.clips,
                command.clipId,
                command.beforeClipId,
            )
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        case "clip.patch":
            return patchById(document.clips, command.clipId, command.patch)
                ? success(document, VALUE_CAPABILITIES)
                : failure(document, "missing-target");
        case "stage.add": {
            const clip = findClip(document, command.clipId);
            if (!clip) return failure(document, "missing-target");
            const invalid = invalidNewEntity(document, command.stage);
            if (invalid) return invalid;
            if (
                !addBefore(
                    clip.stages,
                    clone(command.stage),
                    command.beforeStageId,
                )
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "stage.remove": {
            const clip = findClip(document, command.clipId);
            return clip && removeById(clip.stages, command.stageId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "stage.move": {
            const clip = findClip(document, command.clipId);
            return clip &&
                moveBefore(clip.stages, command.stageId, command.beforeStageId)
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "stage.patch": {
            const clip = findClip(document, command.clipId);
            return clip &&
                patchById(clip.stages, command.stageId, command.patch)
                ? success(document, VALUE_CAPABILITIES)
                : failure(document, "missing-target");
        }
        case "ref.add": {
            const clip = findClip(document, command.clipId);
            if (!clip) return failure(document, "missing-target");
            const invalid = invalidNewEntity(document, command.ref);
            if (invalid) return invalid;
            if (
                !addBefore(clip.refs, clone(command.ref), command.beforeRefId)
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "ref.remove": {
            const clip = findClip(document, command.clipId);
            return clip && removeById(clip.refs, command.refId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "ref.move": {
            const clip = findClip(document, command.clipId);
            return clip &&
                moveBefore(clip.refs, command.refId, command.beforeRefId)
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "ref.patch": {
            const clip = findClip(document, command.clipId);
            return clip && patchById(clip.refs, command.refId, command.patch)
                ? success(document, VALUE_CAPABILITIES)
                : failure(document, "missing-target");
        }
        case "audio-segment.add": {
            const clip = findClip(document, command.clipId);
            if (!clip) return failure(document, "missing-target");
            const invalid = invalidNewEntity(document, command.segment);
            if (invalid) return invalid;
            if (
                !addBefore(
                    clip.audioSegments,
                    clone(command.segment),
                    command.beforeSegmentId,
                )
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "audio-segment.remove": {
            const clip = findClip(document, command.clipId);
            return clip && removeById(clip.audioSegments, command.segmentId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "audio-segment.move": {
            const clip = findClip(document, command.clipId);
            return clip &&
                moveBefore(
                    clip.audioSegments,
                    command.segmentId,
                    command.beforeSegmentId,
                )
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "audio-segment.patch": {
            const clip = findClip(document, command.clipId);
            return clip &&
                patchById(clip.audioSegments, command.segmentId, command.patch)
                ? success(document, VALUE)
                : failure(document, "missing-target");
        }
        case "prompt-window.add": {
            const clip = findClip(document, command.clipId);
            if (!clip) return failure(document, "missing-target");
            const invalid = invalidNewEntity(document, command.window);
            if (invalid) return invalid;
            if (
                !addBefore(
                    clip.promptWindows,
                    clone(command.window),
                    command.beforeWindowId,
                )
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "prompt-window.remove": {
            const clip = findClip(document, command.clipId);
            return clip && removeById(clip.promptWindows, command.windowId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "prompt-window.move": {
            const clip = findClip(document, command.clipId);
            return clip &&
                moveBefore(
                    clip.promptWindows,
                    command.windowId,
                    command.beforeWindowId,
                )
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "prompt-window.patch": {
            const clip = findClip(document, command.clipId);
            return clip &&
                patchById(clip.promptWindows, command.windowId, command.patch)
                ? success(document, VALUE)
                : failure(document, "missing-target");
        }
        case "retake.add": {
            const clip = findClip(document, command.clipId);
            if (!clip) return failure(document, "missing-target");
            if (clip.retake) {
                return failure(document, "retake-already-exists");
            }
            const invalid = invalidNewEntity(document, command.retake);
            if (invalid) return invalid;
            clip.retake = clone(command.retake);
            return success(document, STRUCTURE);
        }
        case "retake.remove": {
            const clip = findClip(document, command.clipId);
            if (!clip || clip.retake?.id !== command.retakeId) {
                return failure(document, "missing-target");
            }
            clip.retake = null;
            return success(document, REMOVE_STRUCTURE);
        }
        case "retake.patch": {
            const clip = findClip(document, command.clipId);
            if (!clip || clip.retake?.id !== command.retakeId) {
                return failure(document, "missing-target");
            }
            Object.assign(clip.retake, clone(command.patch), {
                id: command.retakeId,
            });
            return success(document, VALUE_CAPABILITIES);
        }
        case "audio-track.add": {
            const invalid = invalidNewEntity(document, command.track);
            if (invalid) return invalid;
            if (
                !addBefore(
                    document.audioTracks,
                    clone(command.track),
                    command.beforeTrackId,
                )
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "audio-track.remove":
            return removeById(document.audioTracks, command.trackId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        case "audio-track.move":
            return moveBefore(
                document.audioTracks,
                command.trackId,
                command.beforeTrackId,
            )
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        case "audio-track.patch":
            return patchById(
                document.audioTracks,
                command.trackId,
                command.patch,
            )
                ? success(document, VALUE)
                : failure(document, "missing-target");
        case "audio-span.add": {
            const track = findTrack(document, command.trackId);
            if (!track) return failure(document, "missing-target");
            const invalid = invalidNewEntity(document, command.span);
            if (invalid) return invalid;
            if (
                !addBefore(
                    track.spans,
                    clone(command.span),
                    command.beforeSpanId,
                )
            ) {
                return failure(clone(source), "missing-target");
            }
            return success(document, STRUCTURE);
        }
        case "audio-span.remove": {
            const track = findTrack(document, command.trackId);
            return track && removeById(track.spans, command.spanId)
                ? success(document, REMOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "audio-span.move": {
            const track = findTrack(document, command.trackId);
            return track &&
                moveBefore(track.spans, command.spanId, command.beforeSpanId)
                ? success(document, MOVE_STRUCTURE)
                : failure(document, "missing-target");
        }
        case "audio-span.patch": {
            const track = findTrack(document, command.trackId);
            return track &&
                patchById(track.spans, command.spanId, command.patch)
                ? success(document, VALUE)
                : failure(document, "missing-target");
        }
        default:
            return assertNever(command);
    }
};
