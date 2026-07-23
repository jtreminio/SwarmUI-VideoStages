import { collectAuthoringEntityIds } from "../identity";
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
} from "../types";
import type {
    ChangeImpact,
    CommandFailure,
    DocumentCommandResult,
} from "./types";

export const VALUE: readonly ChangeImpact[] = ["value"];
export const VALUE_CAPABILITIES: readonly ChangeImpact[] = [
    "value",
    "capabilities",
];
export const STRUCTURE: readonly ChangeImpact[] = ["structure", "capabilities"];
export const ARCHITECTURE_CONVERSION: readonly ChangeImpact[] = [
    "value",
    "structure",
    "capabilities",
];
export const ARCHITECTURE_CONVERSION_WITH_SELECTION: readonly ChangeImpact[] = [
    "value",
    "structure",
    "selection",
    "capabilities",
];
export const REMOVE_STRUCTURE: readonly ChangeImpact[] = [
    "structure",
    "selection",
    "capabilities",
];
export const MOVE_STRUCTURE: readonly ChangeImpact[] = ["structure"];
const IMPACT_ORDER: readonly ChangeImpact[] = [
    "value",
    "structure",
    "selection",
    "capabilities",
];

export const clone = <T>(value: T): T => structuredClone(value);

export const findClip = (
    document: CanonicalVideoStagesConfig,
    clipId: string,
): CanonicalClip | null =>
    document.clips.find((clip) => clip.id === clipId) ?? null;

export const findTrack = (
    document: CanonicalVideoStagesConfig,
    trackId: string,
): CanonicalAudioTrack | null =>
    document.audioTracks.find((track) => track.id === trackId) ?? null;

type AuthoringEntity =
    | CanonicalClip
    | CanonicalStage
    | CanonicalRefImage
    | CanonicalAudioSegment
    | CanonicalPromptWindow
    | CanonicalRetake
    | CanonicalAudioTrack
    | CanonicalAudioTrackSpan;

const candidateIds = (entity: AuthoringEntity): string[] => {
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

export const invalidNewEntity = (
    document: CanonicalVideoStagesConfig,
    entity: AuthoringEntity,
): DocumentCommandResult | null => {
    const ids = candidateIds(entity);
    const invalidId = ids.some(
        (id) =>
            typeof id !== "string" ||
            id.trim().length === 0 ||
            id.trim() !== id,
    );
    if (invalidId) return failure(document, "invalid-id");
    if (new Set(ids).size !== ids.length) {
        return failure(document, "duplicate-id");
    }
    const existing = new Set(collectAuthoringEntityIds(document));
    return ids.some((id) => existing.has(id))
        ? failure(document, "duplicate-id")
        : null;
};

export const addBefore = <T extends { id: string }>(
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
    if (beforeIndex < 0) return false;
    items.splice(beforeIndex, 0, item);
    return true;
};

export const removeById = <T extends { id: string }>(
    items: T[],
    id: string,
): boolean => {
    const index = items.findIndex((item) => item.id === id);
    if (index < 0) return false;
    items.splice(index, 1);
    return true;
};

export const moveBefore = <T extends { id: string }>(
    items: T[],
    id: string,
    beforeId: string | null,
): boolean => {
    const fromIndex = items.findIndex((item) => item.id === id);
    if (fromIndex < 0) return false;
    if (beforeId !== null && !items.some((item) => item.id === beforeId)) {
        return false;
    }
    if (id === beforeId) return true;
    const [item] = items.splice(fromIndex, 1);
    if (beforeId === null) {
        items.push(item);
        return true;
    }
    const toIndex = items.findIndex((candidate) => candidate.id === beforeId);
    items.splice(toIndex, 0, item);
    return true;
};

export const patchById = <T extends { id: string }>(
    items: T[],
    id: string,
    patch: Partial<Omit<T, "id">>,
): boolean => {
    const entity = items.find((item) => item.id === id);
    if (!entity) return false;
    Object.assign(entity, clone(patch), { id });
    return true;
};

export const success = (
    document: CanonicalVideoStagesConfig,
    impacts: readonly ChangeImpact[],
): DocumentCommandResult => ({ document, applied: true, impacts });

export const failure = (
    document: CanonicalVideoStagesConfig,
    reason: CommandFailure,
): DocumentCommandResult => ({
    document,
    applied: false,
    impacts: [],
    failure: reason,
});

export const combineImpacts = (
    impacts: readonly (readonly ChangeImpact[])[],
): readonly ChangeImpact[] => {
    const included = new Set(impacts.flat());
    return IMPACT_ORDER.filter((impact) => included.has(impact));
};

export const hasOwn = (value: object, key: PropertyKey): boolean =>
    Object.hasOwn(value, key);
