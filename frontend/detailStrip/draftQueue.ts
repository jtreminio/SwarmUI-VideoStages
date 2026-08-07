import { buildNumber } from "../detailWidgets";
import type { DocumentCommand } from "../documentCommands";
import {
    dispatchDocumentCommand,
    getClips,
    getState,
    getTimelineStore,
    saveClips,
    saveState,
} from "../persistence/repository";
import { setSelection } from "../selection";
import { isVideoStagesEnabled } from "../swarmInputs";
import type { Clip, TimelineSelection, VideoStagesConfig } from "../types";
import type { ClampedNumberOpts } from "./context";
import type { DetailFocusSession } from "./focusSession";

/**
 * A structural edit authored as a named command instead of a whole-document
 * save. The queue owns the dispatch so staleness and selection stay in one
 * place.
 */
export interface StructuralCommand {
    command: DocumentCommand;
    selection: TimelineSelection | "render" | null;
}

export type StructuralOutcome =
    | TimelineSelection
    | "render"
    | null
    | StructuralCommand;

const asCommand = (outcome: StructuralOutcome): StructuralCommand | null =>
    typeof outcome === "object" && outcome !== null && "command" in outcome
        ? outcome
        : null;

interface PendingEntry {
    kind: "clips" | "state";
    mutate: ((clips: Clip[]) => void) | ((state: VideoStagesConfig) => void);
    readBack?: (clips: Clip[]) => number | null;
}

export interface DetailDraftQueue {
    markCurrentSource(): void;
    flush(): void;
    dispose(): void;
    commit(mutate: (clips: Clip[]) => void): void;
    commitState(mutate: (state: VideoStagesConfig) => void): void;
    debouncedCommit(key: string, mutate: (clips: Clip[]) => void): void;
    debouncedCommitState(
        key: string,
        mutate: (state: VideoStagesConfig) => void,
    ): void;
    buildClampedNumber(options: ClampedNumberOpts): HTMLInputElement;
    structuralCommit(
        apply: (clips: Clip[]) => StructuralOutcome,
        options?: { rebuildAfterSelect?: boolean },
    ): void;
}

const DEBOUNCE_MS = 200;

export const createDetailDraftQueue = (options: {
    focus: DetailFocusSession;
    getDock: () => HTMLElement | null;
    isRendering: () => boolean;
    getRenderedSelection: () => TimelineSelection | null;
    syncValueDerivedUi: (selection: TimelineSelection | null) => void;
    render: () => void;
    setSelectionSilently: (selection: TimelineSelection) => void;
}): DetailDraftQueue => {
    let sourceRevision = -1;
    let pendingTimer: ReturnType<typeof setTimeout> | null = null;
    let flushing = false;
    const pending = new Map<string, PendingEntry>();

    const markCurrentSource = (): void => {
        sourceRevision = getTimelineStore().revision();
    };
    const isStale = (): boolean =>
        getTimelineStore().revision() !== sourceRevision;

    const writeBackClamped = (
        entries: [string, PendingEntry][],
        clips: Clip[] | null,
    ): void => {
        const dock = options.getDock();
        if (!dock || !clips) {
            return;
        }
        for (const [key, entry] of entries) {
            if (!entry.readBack) {
                continue;
            }
            const input = dock.querySelector<HTMLInputElement>(
                `input[data-vst-focus-key="${key}"]`,
            );
            const display = entry.readBack(clips);
            if (input && display != null && input.value !== `${display}`) {
                input.value = `${display}`;
            }
        }
    };

    const flush = (): void => {
        if (pendingTimer) {
            clearTimeout(pendingTimer);
            pendingTimer = null;
        }
        if (flushing || pending.size === 0) {
            return;
        }
        const entryList = [...pending.entries()];
        const entries = entryList.map(([, entry]) => entry);
        pending.clear();
        options.focus.capture();
        if (isStale()) {
            return;
        }
        const clipMutations = entries
            .filter((entry) => entry.kind === "clips")
            .map((entry) => entry.mutate as (clips: Clip[]) => void);
        const stateMutations = entries
            .filter((entry) => entry.kind === "state")
            .map((entry) => entry.mutate as (state: VideoStagesConfig) => void);
        flushing = true;
        let flushedClips: Clip[] | null = null;
        try {
            if (clipMutations.length > 0) {
                const clips = getClips();
                for (const mutate of clipMutations) {
                    mutate(clips);
                }
                saveClips(clips, {
                    origin: "detail-strip",
                    valueOnly: true,
                });
                flushedClips = clips;
            }
            if (stateMutations.length > 0) {
                const state = getState();
                for (const mutate of stateMutations) {
                    mutate(state);
                }
                saveState(state, {
                    notifyDomChange: isVideoStagesEnabled(),
                    origin: "detail-strip",
                    valueOnly: true,
                });
            }
            markCurrentSource();
        } finally {
            flushing = false;
        }
        writeBackClamped(entryList, flushedClips);
        options.syncValueDerivedUi(options.getRenderedSelection());
    };

    const schedule = (key: string, entry: PendingEntry): void => {
        if (options.isRendering()) {
            return;
        }
        pending.set(key, entry);
        if (pendingTimer) {
            clearTimeout(pendingTimer);
            pendingTimer = null;
        }
        if (options.focus.isTypingInDock() || options.focus.isSliderGesture()) {
            return;
        }
        pendingTimer = setTimeout(() => {
            pendingTimer = null;
            flush();
        }, DEBOUNCE_MS);
    };

    const commit = (mutate: (clips: Clip[]) => void): void => {
        flush();
        options.focus.capture();
        if (isStale()) {
            options.render();
            return;
        }
        const clips = getClips();
        mutate(clips);
        saveClips(clips, {
            origin: "detail-strip",
            valueOnly: true,
        });
        markCurrentSource();
        options.syncValueDerivedUi(options.getRenderedSelection());
    };

    const commitState = (mutate: (state: VideoStagesConfig) => void): void => {
        flush();
        options.focus.capture();
        if (isStale()) {
            options.render();
            return;
        }
        const state = getState();
        mutate(state);
        saveState(state, {
            notifyDomChange: isVideoStagesEnabled(),
            origin: "detail-strip",
            valueOnly: true,
        });
        markCurrentSource();
        options.syncValueDerivedUi(options.getRenderedSelection());
    };

    const structuralCommit = (
        apply: (clips: Clip[]) => StructuralOutcome,
        structuralOptions?: { rebuildAfterSelect?: boolean },
    ): void => {
        flush();
        if (isStale()) {
            options.render();
            return;
        }
        const snapshot = getTimelineStore().getSnapshot();
        const clips = getClips();
        const outcome = apply(clips);
        if (outcome === null) {
            return;
        }
        const commanded = asCommand(outcome);
        if (commanded) {
            const result = dispatchDocumentCommand(commanded.command, {
                origin: "detail-strip",
                expectedRevision: snapshot.revision,
            });
            if (!result.applied) {
                options.render();
                return;
            }
        } else {
            saveClips(clips, { origin: "detail-strip" });
        }
        markCurrentSource();
        const selection: TimelineSelection | "render" | null = commanded
            ? commanded.selection
            : (outcome as TimelineSelection | "render");
        if (selection === null) {
            return;
        }
        if (selection === "render") {
            options.render();
            return;
        }
        if (structuralOptions?.rebuildAfterSelect) {
            options.setSelectionSilently(selection);
            options.render();
            return;
        }
        setSelection(selection);
    };

    return {
        markCurrentSource,
        flush,
        dispose: () => {
            flush();
            if (pendingTimer) {
                clearTimeout(pendingTimer);
                pendingTimer = null;
            }
            pending.clear();
        },
        commit,
        commitState,
        debouncedCommit: (key, mutate) =>
            schedule(key, { kind: "clips", mutate }),
        debouncedCommitState: (key, mutate) =>
            schedule(key, { kind: "state", mutate }),
        buildClampedNumber: (clampedOptions) => {
            const input = buildNumber(
                clampedOptions.value,
                clampedOptions.min,
                clampedOptions.max,
                clampedOptions.step,
                (value) => {
                    schedule(clampedOptions.key, {
                        kind: "clips",
                        mutate: (clips: Clip[]) =>
                            clampedOptions.mutate(clips, value),
                        readBack: clampedOptions.readBack,
                    });
                },
            );
            input.setAttribute("data-vst-focus-key", clampedOptions.key);
            return input;
        },
        structuralCommit,
    };
};
