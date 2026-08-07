import { getTimelineStore, saveClips } from "./persistence/repository";
import type { AuthoringDocument, Clip } from "./types";

/**
 * Fail-closed guard for an async clip media probe. An operation is bound to one
 * stable clip identity, one media slot on it, and one document revision; any
 * intervening authoring change invalidates it, and a newer probe for the same
 * slot supersedes the older one.
 *
 * The slot is part of the identity because a clip has many probeable slots — its
 * source video and every one of its references. Keying on the clip alone would
 * make two picks in flight cancel each other and silently drop a file.
 */
export interface ClipMediaProbeOperation {
    readonly clipId: string;
    /** Claim once, immediately before applying the async probe result. */
    claim(currentRevision: number): boolean;
    /** Drop a pending operation without applying it. */
    cancel(): void;
}

/** The probeable media slots on a clip. References are identified individually. */
export const INIT_VIDEO_PROBE_SLOT = "init-video";

let nextOperationId = 0;
const currentOperations = new Map<string, number>();

export const findClipByStableId = (
    clips: readonly Clip[],
    clipId: string,
): Clip | undefined => clips.find((clip) => clip.id === clipId);

export const beginClipMediaProbe = (
    clipId: string,
    slot: string,
    revisionAtStart: number,
): ClipMediaProbeOperation => {
    const operationId = ++nextOperationId;
    const key = `${clipId}\n${slot}`;
    currentOperations.set(key, operationId);

    const release = (): void => {
        if (currentOperations.get(key) === operationId) {
            currentOperations.delete(key);
        }
    };

    return {
        clipId,
        claim: (currentRevision): boolean => {
            const current =
                currentRevision === revisionAtStart &&
                currentOperations.get(key) === operationId;
            release();
            return current;
        },
        cancel: release,
    };
};

export interface ClipMediaProbeRun<T> {
    clipId: string;
    slot: string;
    probe: () => Promise<T>;
    /** Mutates the live clip; the caller's edit is saved for it. */
    apply: (clip: Clip, result: T, state: AuthoringDocument) => void;
    onApplied?: () => void;
}

/** Probes, then applies the result to the clip it was started for, or drops it. */
export const runClipMediaProbe = <T>({
    clipId,
    slot,
    probe,
    apply,
    onApplied,
}: ClipMediaProbeRun<T>): void => {
    const store = getTimelineStore();
    const operation = beginClipMediaProbe(
        clipId,
        slot,
        store.getSnapshot().revision,
    );
    void probe().then((result) => {
        if (!operation.claim(store.revision())) {
            return;
        }
        const state = store.getState();
        const clip = findClipByStableId(state.clips, operation.clipId);
        if (!clip) {
            return;
        }
        apply(clip, result, state);
        saveClips(state.clips, { origin: "detail-strip" });
        onApplied?.();
    }, operation.cancel);
};

export const resetClipMediaProbesForTests = (): void => {
    nextOperationId = 0;
    currentOperations.clear();
};
