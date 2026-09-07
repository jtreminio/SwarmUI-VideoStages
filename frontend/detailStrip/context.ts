import type { AuthoringTransactionSnapshot } from "../authoringSnapshot";
import type {
    AuthoringDocument,
    Clip,
    ClipReference,
    UploadedMedia,
} from "../types";
import type { StructuralOutcome } from "./draftQueue";

export interface ClampedNumberOpts {
    key: string;
    value: number;
    min: number;
    max: number;
    step: number;
    readBack: (clips: Clip[]) => number | null;
    mutate: (clips: Clip[], value: number) => void;
}

export interface DetailStripContext {
    commit(mutate: (clips: Clip[]) => void): void;
    commitState(mutate: (state: AuthoringDocument) => void): void;
    debouncedCommit(key: string, mutate: (clips: Clip[]) => void): void;
    debouncedCommitState(
        key: string,
        mutate: (state: AuthoringDocument) => void,
    ): void;
    buildClampedNumber(opts: ClampedNumberOpts): HTMLInputElement;
    structuralCommit(
        apply: (clips: Clip[]) => StructuralOutcome,
        opts?: { rebuildAfterSelect?: boolean },
    ): void;
    render(): void;
    authoring(): AuthoringTransactionSnapshot;

    addRefEntry(clipIdx: number): void;
    addRefEntries?(clipId: string, uploads: readonly UploadedMedia[]): number;
    addClipReferences?(
        clipId: string,
        references: readonly Pick<
            ClipReference,
            "kind" | "uploadedMedia" | "mediaDurationSeconds"
        >[],
    ): number;
    deleteRefEntry(clipIdx: number, refIdx: number): void;
    addClipReference(clipIdx: number): void;
    deleteClipReference(clipIdx: number, referenceIdx: number): void;
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
    getSettingsMode(): string | null;
    setSettingsMode(mode: string | null): void;
}
