import { collectAuthoringEntityIds, ownedIds } from "../identity";
import type {
    CanonicalAudioTrack,
    CanonicalAudioTrackSpan,
    CanonicalAuthoringDocument,
    CanonicalClip,
    CanonicalFrameRefImage,
    CanonicalPromptWindow,
    CanonicalRetake,
    CanonicalStage,
} from "../types";
import type { CommandFailure, DocumentCommandResult } from "./types";

export const clone = <T>(value: T): T => structuredClone(value);

export const findClip = (
    document: CanonicalAuthoringDocument,
    clipId: string,
): CanonicalClip | null =>
    document.clips.find((clip) => clip.id === clipId) ?? null;

export const findTrack = (
    document: CanonicalAuthoringDocument,
    trackId: string,
): CanonicalAudioTrack | null =>
    document.audioTracks.find((track) => track.id === trackId) ?? null;

type AuthoringEntity =
    | CanonicalClip
    | CanonicalStage
    | CanonicalFrameRefImage
    | CanonicalPromptWindow
    | CanonicalRetake
    | CanonicalAudioTrack
    | CanonicalAudioTrackSpan;

export const invalidNewEntity = (
    document: CanonicalAuthoringDocument,
    entity: AuthoringEntity,
): DocumentCommandResult | null => {
    const ids = ownedIds(entity);
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
    const existing = new Set<string | undefined>(
        collectAuthoringEntityIds(document),
    );
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
    document: CanonicalAuthoringDocument,
): DocumentCommandResult => ({ document, applied: true });

export const failure = (
    document: CanonicalAuthoringDocument,
    reason: CommandFailure,
): DocumentCommandResult => ({
    document,
    applied: false,
    failure: reason,
});

export const hasOwn = (value: object, key: PropertyKey): boolean =>
    Object.hasOwn(value, key);
