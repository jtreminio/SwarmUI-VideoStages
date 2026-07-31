import type { AuthoringTransactionSnapshot } from "../authoringSnapshot";
import type { Clip, TimelineSelection, VideoStagesConfig } from "../types";

export interface ClampedNumberOpts {
    key: string;
    value: number;
    min: number;
    max: number;
    step: number;
    readBack: (clips: Clip[]) => number | null;
    mutate: (clips: Clip[], value: number) => void;
}

/**
 * The detail strip's command, draft, selection, focus, and render services.
 * Panel builders receive the current document separately and use `authoring`
 * for the matching catalog/defaults projection, so this contract does not
 * expose ambient repository readers.
 */
export interface DetailStripContext {
    commit(mutate: (clips: Clip[]) => void): void;
    commitState(mutate: (state: VideoStagesConfig) => void): void;
    debouncedCommit(key: string, mutate: (clips: Clip[]) => void): void;
    debouncedCommitState(
        key: string,
        mutate: (state: VideoStagesConfig) => void,
    ): void;
    buildClampedNumber(opts: ClampedNumberOpts): HTMLInputElement;
    structuralCommit(
        apply: (clips: Clip[]) => TimelineSelection | "render" | null,
        opts?: { rebuildAfterSelect?: boolean },
    ): void;
    render(): void;
    authoring(): AuthoringTransactionSnapshot;

    addRefEntry(clipIdx: number): void;
    deleteRefEntry(clipIdx: number, refIdx: number): void;
    addPromptWindow(clipIdx: number): void;
    deleteWindowEntry(clipIdx: number, windowIdx: number): void;
    createRetake(clipIdx: number): void;
    removeRetake(clipIdx: number): void;
    deleteClip?(clipIdx: number): void;
    addStage(clipIdx: number): void;
    deleteStage(clipIdx: number, stageIdx: number): void;
    selectStage(clipIdx: number, stageIdx: number): void;
    toggleClipSkip(clipIdx: number): void;
    toggleStageSkip(clipIdx: number, stageIdx: number): void;

    getBoundBody(): HTMLElement | null;
    getDockEl(): HTMLElement | null;
    getSettingsMode(): string | null;
    setSettingsMode(mode: string | null): void;
}
