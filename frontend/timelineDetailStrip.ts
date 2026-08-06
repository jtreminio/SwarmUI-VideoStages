import {
    type AuthoringTransactionSnapshot,
    captureAuthoringTransactionSnapshot,
} from "./authoringSnapshot";
import type { DetailStripContext } from "./detailStrip/context";
import {
    createDetailDraftQueue,
    type DetailDraftQueue,
} from "./detailStrip/draftQueue";
import { createDetailFocusSession } from "./detailStrip/focusSession";
import {
    clampDetailSelection,
    detailBreadcrumb,
} from "./detailStrip/panelRouter";
import { renderDetailShell } from "./detailStrip/renderShell";
import { createDetailSelectionOperations } from "./detailStrip/selectionOperations";
import { closeTimelineAuthoringSettingsModal } from "./detailStrip/settingsModal";
import { getState } from "./persistence/repository";
import {
    getSelection,
    isSameSelection,
    setSelection,
    subscribeSelection,
} from "./selection";
import type { UpdateMeta } from "./store";
import type { TimelineSelection } from "./types";

const DETAIL_CLASS = "vst-detail";

export interface TimelineDetailStrip {
    /**
     * @param body listener-host carrying capture-phase timeline chip listeners.
     * @param dock render-host owned by the caller.
     */
    attach(
        body: HTMLElement,
        dock: HTMLElement,
        renderImmediately?: boolean,
    ): void;
    render(meta?: UpdateMeta, snapshot?: AuthoringTransactionSnapshot): void;
    flushPending(): void;
    dispose(): void;
}

export const createTimelineDetailStrip = (): TimelineDetailStrip => {
    let boundBody: HTMLElement | null = null;
    let dockEl: HTMLElement | null = null;
    let unsubscribe: (() => void) | null = null;
    let rendering = false;
    let suppressSelectionRender = false;
    let settingsMode: string | null = null;
    let revealSelectionOnNextRender = false;
    let renderEnabled = true;
    let activeSnapshot: AuthoringTransactionSnapshot | null = null;
    let draftQueue: DetailDraftQueue;
    let renderImplementation: (
        meta: UpdateMeta | undefined,
        snapshot: AuthoringTransactionSnapshot,
    ) => void = () => {};
    const render = (
        meta?: UpdateMeta,
        snapshot = captureAuthoringTransactionSnapshot(),
    ): void => {
        renderEnabled = true;
        renderImplementation(meta, snapshot);
    };

    let renderedSelection: TimelineSelection | null = null;
    const focus = createDetailFocusSession({
        getDock: () => dockEl,
        isRendering: () => rendering,
        flushPending: () => draftQueue?.flush(),
    });
    const syncValueDerivedUi = (selection: TimelineSelection | null): void => {
        if (!selection || !dockEl || !renderedSelection) {
            return;
        }
        const clips = getState().clips;
        const breadcrumb =
            dockEl.querySelector<HTMLElement>(".vst-detail-crumb");
        if (breadcrumb) {
            breadcrumb.textContent = detailBreadcrumb(renderedSelection, clips);
        }
    };
    draftQueue = createDetailDraftQueue({
        focus,
        getDock: () => dockEl,
        isRendering: () => rendering,
        getRenderedSelection: () => renderedSelection,
        syncValueDerivedUi,
        render,
        setSelectionSilently: (selection) => {
            suppressSelectionRender = true;
            setSelection(selection);
            suppressSelectionRender = false;
        },
    });
    const selectionOperations = createDetailSelectionOperations(
        draftQueue.structuralCommit,
        captureAuthoringTransactionSnapshot,
    );

    const context: DetailStripContext = {
        commit: draftQueue.commit,
        commitState: draftQueue.commitState,
        debouncedCommit: draftQueue.debouncedCommit,
        debouncedCommitState: draftQueue.debouncedCommitState,
        buildClampedNumber: draftQueue.buildClampedNumber,
        structuralCommit: draftQueue.structuralCommit,
        render,
        authoring: () =>
            activeSnapshot ?? captureAuthoringTransactionSnapshot(),
        addRefEntry: selectionOperations.addRefEntry,
        deleteRefEntry: selectionOperations.deleteRefEntry,
        addClipReference: selectionOperations.addClipReference,
        deleteClipReference: selectionOperations.deleteClipReference,
        addPromptWindow: selectionOperations.addPromptWindow,
        deleteWindowEntry: selectionOperations.deleteWindowEntry,
        createRetake: selectionOperations.createRetake,
        removeRetake: selectionOperations.removeRetake,
        deleteClip: selectionOperations.deleteClip,
        addStage: selectionOperations.addStage,
        deleteStage: selectionOperations.deleteStage,
        selectStage: selectionOperations.selectStage,
        toggleClipSkip: selectionOperations.toggleClipSkip,
        toggleStageSkip: selectionOperations.toggleStageSkip,
        getBoundBody: () => boundBody,
        getDockEl: () => dockEl,
        getSettingsMode: () => settingsMode,
        setSettingsMode: (mode) => {
            settingsMode = mode;
        },
    };

    const ensureDetail = (): HTMLElement => {
        if (!dockEl) {
            throw new Error("detail strip not attached");
        }
        return dockEl;
    };

    renderImplementation = (
        meta: UpdateMeta | undefined,
        snapshot: AuthoringTransactionSnapshot,
    ): void => {
        if (!dockEl) {
            return;
        }
        activeSnapshot = snapshot;
        try {
            if (
                meta?.origin === "detail-strip" &&
                meta.hint === "value-only" &&
                renderedSelection &&
                isSameSelection(getSelection(), renderedSelection)
            ) {
                draftQueue.markCurrentSource();
                syncValueDerivedUi(renderedSelection);
                return;
            }

            // Load-bearing: pending field edits flush before widget teardown or
            // synthetic slider setup can observe stale carrier state.
            draftQueue.flush();
            rendering = true;
            draftQueue.markCurrentSource();
            const detail = ensureDetail();
            const state = getState();
            const clips = state.clips;
            const rawSelection = getSelection();
            const selection = clampDetailSelection(
                rawSelection,
                clips,
                state.audioTracks,
            );
            if (!isSameSelection(rawSelection, selection)) {
                setSelection(selection);
                return;
            }

            const revealSelection = revealSelectionOnNextRender;
            revealSelectionOnNextRender = false;
            renderDetailShell({
                detail,
                context,
                focus,
                state,
                selection,
                revealSelection,
            });
            renderedSelection = selection;
        } finally {
            rendering = false;
            activeSnapshot = null;
        }
    };

    const onSelectionChanged = (): void => {
        if (suppressSelectionRender || !renderEnabled) {
            return;
        }
        focus.beginSelectionSession();
        settingsMode = null;
        const active = document.activeElement;
        revealSelectionOnNextRender = !(
            active instanceof HTMLElement && dockEl?.contains(active)
        );
        render();
    };

    const dispose = (): void => {
        draftQueue.dispose();
        closeTimelineAuthoringSettingsModal();
        focus.reset();
        document.removeEventListener(
            "pointerdown",
            focus.onDocumentPointerDown,
            true,
        );
        document.removeEventListener(
            "pointerup",
            focus.onDocumentPointerUp,
            true,
        );
        document.removeEventListener(
            "pointercancel",
            focus.onDocumentPointerUp,
            true,
        );
        unsubscribe?.();
        unsubscribe = null;
        if (boundBody) {
            boundBody.removeEventListener(
                "mousedown",
                selectionOperations.onMouseDownCapture,
                true,
            );
            boundBody.removeEventListener(
                "click",
                selectionOperations.onClickCapture,
                true,
            );
            boundBody.removeEventListener(
                "keydown",
                selectionOperations.onKeyDownCapture,
                true,
            );
            boundBody = null;
        }
        if (dockEl) {
            dockEl.removeEventListener(
                "keydown",
                selectionOperations.onStripKeyDown,
            );
            dockEl.removeEventListener("focusout", focus.onDockFocusOut);
            dockEl.removeEventListener("focusin", focus.onDockFocusIn);
            dockEl.removeEventListener("change", focus.onDockChange);
            dockEl.className = DETAIL_CLASS;
            dockEl.innerHTML = "";
            dockEl = null;
        }
        renderedSelection = null;
        renderEnabled = false;
    };

    const attach = (
        body: HTMLElement,
        dock: HTMLElement,
        renderImmediately = true,
    ): void => {
        // Both assignments are load-bearing: this one is the only one a re-attach
        // to the same binding reaches, and the one after dispose() restores the
        // mode dispose() just cleared.
        renderEnabled = renderImmediately;
        if (boundBody === body && dockEl === dock) {
            return;
        }
        dispose();
        boundBody = body;
        dockEl = dock;
        renderEnabled = renderImmediately;
        body.addEventListener(
            "mousedown",
            selectionOperations.onMouseDownCapture,
            true,
        );
        body.addEventListener(
            "click",
            selectionOperations.onClickCapture,
            true,
        );
        body.addEventListener(
            "keydown",
            selectionOperations.onKeyDownCapture,
            true,
        );
        dock.addEventListener("keydown", selectionOperations.onStripKeyDown);
        dock.addEventListener("focusout", focus.onDockFocusOut);
        dock.addEventListener("focusin", focus.onDockFocusIn);
        dock.addEventListener("change", focus.onDockChange);
        document.addEventListener(
            "pointerdown",
            focus.onDocumentPointerDown,
            true,
        );
        document.addEventListener("pointerup", focus.onDocumentPointerUp, true);
        document.addEventListener(
            "pointercancel",
            focus.onDocumentPointerUp,
            true,
        );
        unsubscribe = subscribeSelection(onSelectionChanged);
        if (renderImmediately) {
            render();
        }
    };

    return {
        attach,
        render,
        flushPending: () => draftQueue.flush(),
        dispose,
    };
};
