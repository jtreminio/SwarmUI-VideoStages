import {
    type CanonicalVideoStagesConfig,
    CURRENT_AUTHORING_SCHEMA_VERSION,
    type VideoStagesConfig,
} from "./types";

export type EntityKind =
    | "clip"
    | "stage"
    | "ref"
    | "clip_reference"
    | "ic_lora"
    | "prompt_window"
    | "retake"
    | "audio_track"
    | "audio_span";

interface MaybeIdentified {
    id?: string;
}

interface IdentityEntry {
    entity: MaybeIdentified;
    kind: EntityKind;
    repairPath: string;
}

let fallbackSequence = 0;

const normalizedExistingId = (value: unknown): string | null => {
    if (typeof value !== "string") {
        return null;
    }
    const id = value.trim();
    return id.length > 0 ? id : null;
};

export const createEntityId = (kind: EntityKind): string => {
    const randomUuid = globalThis.crypto?.randomUUID?.();
    if (randomUuid) {
        return `${kind}_${randomUuid}`;
    }
    fallbackSequence += 1;
    return `${kind}_${Date.now().toString(36)}_${fallbackSequence.toString(36)}`;
};

const assignUniqueId = (
    entry: IdentityEntry,
    reserved: ReadonlyMap<MaybeIdentified, string>,
    used: Set<string>,
): string => {
    const reservedId = reserved.get(entry.entity);
    if (reservedId) {
        entry.entity.id = reservedId;
        return reservedId;
    }

    // `_legacy_` is retained in the persisted ID format for stability.
    const base = `${entry.kind}_legacy_${entry.repairPath}`;
    let id = base;
    let collision = 1;
    while (used.has(id)) {
        collision += 1;
        id = `${base}_${collision}`;
    }
    entry.entity.id = id;
    used.add(id);
    return id;
};

const clipIdentityEntries = (
    clips: VideoStagesConfig["clips"],
): IdentityEntry[] => {
    const entries: IdentityEntry[] = [];
    for (let clipIndex = 0; clipIndex < clips.length; clipIndex++) {
        const clip = clips[clipIndex];
        entries.push({
            entity: clip,
            kind: "clip",
            repairPath: `${clipIndex}`,
        });
        for (
            let stageIndex = 0;
            stageIndex < clip.stages.length;
            stageIndex++
        ) {
            entries.push({
                entity: clip.stages[stageIndex],
                kind: "stage",
                repairPath: `${clipIndex}_${stageIndex}`,
            });
        }
        for (let refIndex = 0; refIndex < clip.frameRefs.length; refIndex++) {
            entries.push({
                entity: clip.frameRefs[refIndex],
                kind: "ref",
                repairPath: `${clipIndex}_${refIndex}`,
            });
        }
        for (
            let referenceIndex = 0;
            referenceIndex < clip.references.length;
            referenceIndex++
        ) {
            entries.push({
                entity: clip.references[referenceIndex],
                kind: "clip_reference",
                repairPath: `${clipIndex}_${referenceIndex}`,
            });
        }
        for (
            let icLoraIndex = 0;
            icLoraIndex < clip.icLoras.length;
            icLoraIndex++
        ) {
            entries.push({
                entity: clip.icLoras[icLoraIndex],
                kind: "ic_lora",
                repairPath: `${clipIndex}_${icLoraIndex}`,
            });
        }
        for (
            let windowIndex = 0;
            windowIndex < clip.promptWindows.length;
            windowIndex++
        ) {
            entries.push({
                entity: clip.promptWindows[windowIndex],
                kind: "prompt_window",
                repairPath: `${clipIndex}_${windowIndex}`,
            });
        }
        if (clip.retake) {
            entries.push({
                entity: clip.retake,
                kind: "retake",
                repairPath: `${clipIndex}`,
            });
        }
    }
    return entries;
};

const audioTrackIdentityEntries = (
    tracks: NonNullable<VideoStagesConfig["audioTracks"]>,
): IdentityEntry[] => {
    const entries: IdentityEntry[] = [];
    for (let trackIndex = 0; trackIndex < tracks.length; trackIndex++) {
        const track = tracks[trackIndex];
        entries.push({
            entity: track,
            kind: "audio_track",
            repairPath: `${trackIndex}`,
        });
        for (let spanIndex = 0; spanIndex < track.spans.length; spanIndex++) {
            entries.push({
                entity: track.spans[spanIndex],
                kind: "audio_span",
                repairPath: `${trackIndex}_${spanIndex}`,
            });
        }
    }
    return entries;
};

const assignEntryIdentities = (
    entries: readonly IdentityEntry[],
    used: Set<string>,
): void => {
    // Reserve every first valid existing occurrence before filling any gaps.
    // Otherwise an earlier missing positional ID can steal a later entity's
    // deterministic compatibility ID and silently retarget stable references.
    const reserved = new Map<MaybeIdentified, string>();
    for (const { entity } of entries) {
        const existing = normalizedExistingId(entity.id);
        if (existing && !used.has(existing)) {
            reserved.set(entity, existing);
            used.add(existing);
        }
    }
    for (const entry of entries) {
        assignUniqueId(entry, reserved, used);
    }
};

/**
 * Adds and de-duplicates every clip-owned identity in place. This is the
 * repair seam for UI constructors that may create an entity without an id
 * before the next save.
 */
export const ensureClipEntityIdentities = (
    clips: VideoStagesConfig["clips"],
    seen: Set<string> = new Set<string>(),
): Set<string> => {
    assignEntryIdentities(clipIdentityEntries(clips), seen);
    return seen;
};

/**
 * Repairs the current-schema working config into the canonical versioned
 * authoring document in place. Existing unique IDs survive; missing, blank, or
 * duplicate IDs are replaced once and then persist through normal carriers.
 */
export function ensureAuthoringDocumentIdentity(
    state: VideoStagesConfig,
): asserts state is CanonicalVideoStagesConfig {
    state.schemaVersion = CURRENT_AUTHORING_SCHEMA_VERSION;
    state.audioTracks ??= [];
    assignEntryIdentities(
        [
            ...clipIdentityEntries(state.clips),
            ...audioTrackIdentityEntries(state.audioTracks),
        ],
        new Set<string>(),
    );
}

interface IdCarrier {
    id?: string;
}

interface ClipIds extends IdCarrier {
    stages: readonly IdCarrier[];
    frameRefs: readonly IdCarrier[];
    references?: readonly IdCarrier[];
    icLoras?: readonly IdCarrier[];
    promptWindows: readonly IdCarrier[];
    retake?: IdCarrier | null;
}

interface AudioTrackIds extends IdCarrier {
    spans: readonly IdCarrier[];
}

/**
 * Every id an entity owns, itself first, unfiltered so a caller can still see
 * an empty one. The document-wide walk and the new-entity collision check both
 * enumerate through here, so neither can grow a list the other forgets.
 */
export const ownedIds = (
    entity: IdCarrier | ClipIds | AudioTrackIds,
): (string | undefined)[] => {
    const nested: readonly IdCarrier[] =
        "stages" in entity
            ? [
                  ...entity.stages,
                  ...entity.frameRefs,
                  ...(entity.references ?? []),
                  ...(entity.icLoras ?? []),
                  ...entity.promptWindows,
                  ...(entity.retake ? [entity.retake] : []),
              ]
            : "spans" in entity
              ? entity.spans
              : [];
    return [entity.id, ...nested.map((item) => item.id)];
};

export const collectAuthoringEntityIds = (state: VideoStagesConfig): string[] =>
    [
        ...state.clips.flatMap(ownedIds),
        ...(state.audioTracks ?? []).flatMap(ownedIds),
    ].filter((id): id is string => !!id);
