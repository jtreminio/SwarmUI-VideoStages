/**
 * Selection is stored as a stable-identity ANCHOR and resolved to array
 * positions on every read, so a structural edit or a history restore can never
 * silently rebind the dock to a different entity.
 *
 * An anchor is the index-shaped selection the caller asked for, plus the ids of
 * the entities that selection addressed at the moment it was made:
 *   - `ownerId`: the owning clip (or the audio track, for `audio-track`),
 *   - `itemId`: the addressed child (stage, ref, relay window).
 *
 * Resolution is deliberately conservative: an id that was never captured (an
 * entity the document has not identified yet, or a selection made against an
 * empty document) falls straight through to its recorded index, so anchoring
 * only ever CHANGES behaviour for selections that really were identified.
 * A captured id that no longer exists degrades predictably — to the nearest
 * surviving sibling, else to the owning clip or `none` — instead of pointing at
 * whichever entity happens to occupy the old slot.
 */

import { getState } from "./persistence/repository";
import type { TimelineSelection } from "./selectionTypes";
import type { Clip, VideoStagesConfig } from "./types";

/**
 * The one delete-then-reselect convention: keep the nearest surviving sibling
 * (the same slot, or the new last one), else fall back to the owning entity.
 * Every removal path routes through this — the detail strip's `commitRemoval`,
 * the timeline's clip delete, the tracks' shift-click deletes, and the
 * degradation branch of anchor resolution.
 */
export const selectionAfterRemoval = (
    index: number,
    remaining: number,
    neighbour: (index: number) => TimelineSelection,
    fallback: TimelineSelection,
): TimelineSelection =>
    remaining > 0 ? neighbour(Math.min(index, remaining - 1)) : fallback;

export interface SelectionAnchor {
    selection: TimelineSelection;
    ownerId?: string;
    itemId?: string;
}

interface Identified {
    id?: string;
}

const idAt = (
    list: readonly Identified[] | undefined,
    index: number,
): string | undefined => list?.[index]?.id ?? undefined;

const clipOwnerIndex = (selection: TimelineSelection): number | null => {
    switch (selection.kind) {
        case "none":
        case "audio-track":
            return null;
        case "boundary":
        case "boundary-ref":
            return selection.leftClipIdx;
        default:
            return selection.clipIdx;
    }
};

/** The child list a selection addresses inside its owning clip, if any. */
const clipItemList = (
    selection: TimelineSelection,
    clip: Clip | undefined,
): { list: readonly Identified[]; index: number } | null => {
    if (!clip) {
        return null;
    }
    switch (selection.kind) {
        case "clip":
            return { list: clip.stages, index: selection.stageIdx };
        case "ref":
            return { list: clip.frameRefs, index: selection.refIdx };
        case "clip-ref":
            return { list: clip.references, index: selection.referenceIdx };
        case "ic-lora":
            return { list: clip.icLoras, index: selection.entryIdx };
        case "prompt-minor":
            return {
                list: clip.promptWindows ?? [],
                index: selection.windowIdx,
            };
        default:
            return null;
    }
};

/** Captures the stable ids the given index-shaped selection currently addresses. */
export const anchorSelection = (
    selection: TimelineSelection,
    stateOverride?: VideoStagesConfig,
): SelectionAnchor => {
    if (selection.kind === "none") {
        return { selection };
    }
    const state = stateOverride ?? getState();
    if (selection.kind === "audio-track") {
        return {
            selection,
            ownerId: idAt(state.audioTracks, selection.trackIdx),
        };
    }
    const ownerIdx = clipOwnerIndex(selection);
    if (ownerIdx === null) {
        return { selection };
    }
    const clip = state.clips[ownerIdx];
    const item = clipItemList(selection, clip);
    return {
        selection,
        ownerId: clip?.id ?? undefined,
        itemId: item ? idAt(item.list, item.index) : undefined,
    };
};

/**
 * Index of `id` in `list`, or null when the id was captured but is gone.
 * A missing id means "never identified" and falls through to `hint`.
 */
const resolveIndex = (
    list: readonly Identified[],
    id: string | undefined,
    hint: number,
): number | null => {
    if (id === undefined) {
        return hint;
    }
    const found = list.findIndex((entry) => entry.id === id);
    return found >= 0 ? found : null;
};

const withClipIndex = (
    selection: TimelineSelection,
    clipIdx: number,
): TimelineSelection => {
    switch (selection.kind) {
        case "none":
        case "audio-track":
            return selection;
        case "boundary":
            return { kind: "boundary", leftClipIdx: clipIdx };
        case "boundary-ref":
            return { kind: "boundary-ref", leftClipIdx: clipIdx };
        default:
            return { ...selection, clipIdx };
    }
};

const withItemIndex = (
    selection: TimelineSelection,
    clipIdx: number,
    itemIdx: number,
): TimelineSelection => {
    switch (selection.kind) {
        case "clip":
            return { kind: "clip", clipIdx, stageIdx: itemIdx };
        case "ref":
            return { kind: "ref", clipIdx, refIdx: itemIdx };
        case "clip-ref":
            return { kind: "clip-ref", clipIdx, referenceIdx: itemIdx };
        case "ic-lora":
            return { kind: "ic-lora", clipIdx, entryIdx: itemIdx };
        case "prompt-minor":
            return { kind: "prompt-minor", clipIdx, windowIdx: itemIdx };
        default:
            return withClipIndex(selection, clipIdx);
    }
};

/** Where a selection lands when its own entity is gone and it has no siblings. */
const itemFallback = (
    selection: TimelineSelection,
    clipIdx: number,
): TimelineSelection =>
    selection.kind === "clip" || selection.kind === "ic-lora"
        ? { kind: "clip", clipIdx, stageIdx: 0 }
        : { kind: "none" };

export const resolveSelection = (
    anchor: SelectionAnchor,
    stateOverride?: VideoStagesConfig,
): TimelineSelection => {
    const selection = anchor.selection;
    // Nothing was identified, so there is nothing to resolve — and no reason to
    // touch the document at all.
    if (anchor.ownerId === undefined && anchor.itemId === undefined) {
        return selection;
    }
    const state = stateOverride ?? getState();
    if (selection.kind === "audio-track") {
        const tracks = state.audioTracks ?? [];
        const trackIdx = resolveIndex(
            tracks,
            anchor.ownerId,
            selection.trackIdx,
        );
        return trackIdx === null
            ? selectionAfterRemoval(
                  selection.trackIdx,
                  tracks.length,
                  (index) => ({ kind: "audio-track", trackIdx: index }),
                  { kind: "none" },
              )
            : { kind: "audio-track", trackIdx };
    }
    const ownerHint = clipOwnerIndex(selection);
    if (ownerHint === null) {
        return selection;
    }
    const clipIdx = resolveIndex(state.clips, anchor.ownerId, ownerHint);
    if (clipIdx === null) {
        // The owning clip is gone. A whole-clip selection keeps the delete
        // convention and moves to the nearest surviving clip; anything nested
        // in the clip has nothing left to address.
        return selection.kind === "clip"
            ? selectionAfterRemoval(
                  ownerHint,
                  state.clips.length,
                  (index) => ({ kind: "clip", clipIdx: index, stageIdx: 0 }),
                  { kind: "none" },
              )
            : { kind: "none" };
    }
    const clip = state.clips[clipIdx];
    const item = clipItemList(selection, clip);
    if (!item) {
        return withClipIndex(selection, clipIdx);
    }
    const itemIdx = resolveIndex(item.list, anchor.itemId, item.index);
    return itemIdx === null
        ? selectionAfterRemoval(
              item.index,
              item.list.length,
              (index) => withItemIndex(selection, clipIdx, index),
              itemFallback(selection, clipIdx),
          )
        : withItemIndex(selection, clipIdx, itemIdx);
};

/**
 * Two anchors address the same entity. The recorded indexes are compared AFTER
 * resolution so an anchor whose hint went stale (its entity moved) still
 * compares equal to a freshly made anchor for that same entity.
 */
export const sameAnchor = (
    a: SelectionAnchor,
    b: SelectionAnchor,
    state?: VideoStagesConfig,
): boolean =>
    a.ownerId === b.ownerId &&
    a.itemId === b.itemId &&
    sameSelectionShape(resolveSelection(a, state), resolveSelection(b, state));

const sameSelectionShape = (
    a: TimelineSelection,
    b: TimelineSelection,
): boolean => {
    if (a.kind !== b.kind) {
        return false;
    }
    switch (a.kind) {
        case "none":
            return true;
        case "boundary":
            return a.leftClipIdx === (b as typeof a).leftClipIdx;
        case "boundary-ref":
            return a.leftClipIdx === (b as typeof a).leftClipIdx;
        case "audio-track":
            return a.trackIdx === (b as typeof a).trackIdx;
        case "clip":
            return (
                a.clipIdx === (b as typeof a).clipIdx &&
                a.stageIdx === (b as typeof a).stageIdx
            );
        case "ref":
            return (
                a.clipIdx === (b as typeof a).clipIdx &&
                a.refIdx === (b as typeof a).refIdx
            );
        case "clip-ref":
            return (
                a.clipIdx === (b as typeof a).clipIdx &&
                a.referenceIdx === (b as typeof a).referenceIdx
            );
        case "ic-lora":
            return (
                a.clipIdx === (b as typeof a).clipIdx &&
                a.entryIdx === (b as typeof a).entryIdx
            );
        case "prompt-minor":
            return (
                a.clipIdx === (b as typeof a).clipIdx &&
                a.windowIdx === (b as typeof a).windowIdx
            );
        default:
            return a.clipIdx === (b as { clipIdx: number }).clipIdx;
    }
};

export const isSameSelection = sameSelectionShape;
