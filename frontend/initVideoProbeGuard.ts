import type { Clip } from "./types";

/**
 * Fail-closed guard for a initVideoClip metadata probe. The operation is bound
 * to one stable clip identity and one document revision; any intervening
 * authoring change invalidates it. A newer probe for the same clip also
 * supersedes the older operation.
 */
export interface InitVideoProbeOperation {
    readonly clipId: string;
    /** Claim once, immediately before applying the async probe result. */
    claim(currentRevision: number): boolean;
    /** Drop a pending operation without applying it. */
    cancel(): void;
}

let nextOperationId = 0;
const currentOperations = new Map<string, number>();

export const findClipByStableId = (
    clips: readonly Clip[],
    clipId: string,
): Clip | undefined => clips.find((clip) => clip.id === clipId);

export const beginInitVideoProbeOperation = (
    clipId: string,
    revisionAtStart: number,
): InitVideoProbeOperation => {
    const operationId = ++nextOperationId;
    currentOperations.set(clipId, operationId);

    const release = (): void => {
        if (currentOperations.get(clipId) === operationId) {
            currentOperations.delete(clipId);
        }
    };

    return {
        clipId,
        claim: (currentRevision): boolean => {
            const current =
                currentRevision === revisionAtStart &&
                currentOperations.get(clipId) === operationId;
            release();
            return current;
        },
        cancel: release,
    };
};

export const resetInitVideoProbeOperationsForTests = (): void => {
    nextOperationId = 0;
    currentOperations.clear();
};
