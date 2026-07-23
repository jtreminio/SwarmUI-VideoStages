import type { CapabilityViewResolver } from "../architectures/policy";
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
 * The factory's commit pipeline, structural ops and closure-state accessors,
 * passed explicitly to each panel builder instead of captured. State readers
 * that are plain module functions (getClips/getState/getRootDefaults,
 * setSelection) are imported directly by the panels; only genuine factory
 * state (the listener/render hosts, the settings resolution mode) rides here.
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
    capabilities(): CapabilityViewResolver;
    generatedEntryMode(): "text-to-video" | "image-to-video";

    addRefEntry(clipIdx: number): void;
    deleteRefEntry(clipIdx: number, refIdx: number): void;
    addPromptWindow(clipIdx: number): void;
    deleteWindowEntry(clipIdx: number, windowIdx: number): void;
    createRetake(clipIdx: number): void;
    removeRetake(clipIdx: number): void;
    addAudioSegment(clipIdx: number): void;
    removeAudioSegment(clipIdx: number, segIdx: number): void;
    addStage(clipIdx: number): void;
    deleteStage(clipIdx: number, stageIdx: number): void;
    selectStage(clipIdx: number, stageIdx: number): void;

    getBoundBody(): HTMLElement | null;
    getDockEl(): HTMLElement | null;
    getSettingsMode(): string | null;
    setSettingsMode(mode: string | null): void;
}
