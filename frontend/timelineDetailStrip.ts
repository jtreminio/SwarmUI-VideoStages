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
import { releaseSidebarMediaPreviews } from "./detailStrip/sidebarMediaPreview";
import { closeTrimModal } from "./detailStrip/trimModal";
import { resetRememberedAccordionSections } from "./detailWidgets";
import { readDroppedReferenceMedia } from "./droppedReferenceMedia";
import { fileAsDataUri } from "./fileDataUri";
import { collectDroppedFiles, hasDroppedFiles } from "./fileDrop";
import { getVideoStagesHostBridge } from "./host";
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
const REFERENCE_DROP_TARGET = ".vst-detail-add-ref, .vst-detail-add-clip-ref";
const FILE_DROP_HOVER_CLASS = "vst-file-drop-hover";

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
    let activeFileDropTarget: HTMLButtonElement | null = null;
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
    const authoring = (): AuthoringTransactionSnapshot =>
        activeSnapshot ?? captureAuthoringTransactionSnapshot();

    let renderedSelection: TimelineSelection | null = null;
    const focus = createDetailFocusSession({
        getDock: () => dockEl,
        isRendering: () => rendering,
        flushPending: () => draftQueue?.flush(),
    });
    const syncValueDerivedUi = (selection: TimelineSelection | null): void => {
        if (!selection || !dockEl) {
            return;
        }
        const state = getState();
        const breadcrumb =
            dockEl.querySelector<HTMLElement>(".vst-detail-crumb");
        if (breadcrumb) {
            breadcrumb.textContent = detailBreadcrumb(
                selection,
                state.clips,
                state.fps,
                authoring().capabilities,
            );
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
        authoring,
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

    const referenceDropButton = (
        event: DragEvent,
    ): HTMLButtonElement | null => {
        const button =
            event.target instanceof Element
                ? event.target.closest<HTMLButtonElement>(REFERENCE_DROP_TARGET)
                : null;
        return button?.disabled === false ? button : null;
    };
    const setFileDropTarget = (button: HTMLButtonElement | null): void => {
        if (activeFileDropTarget === button) {
            return;
        }
        activeFileDropTarget?.classList.remove(FILE_DROP_HOVER_CLASS);
        activeFileDropTarget = button;
        activeFileDropTarget?.classList.add(FILE_DROP_HOVER_CLASS);
    };
    const dropTarget = (
        button: HTMLButtonElement,
    ): { clipId: string; kind: "keyframe" | "clip-reference" } | null => {
        const clipId = button.dataset.vstClipId;
        if (!clipId) {
            return null;
        }
        const kind = button.classList.contains("vst-detail-add-ref")
            ? "keyframe"
            : "clip-reference";
        return { clipId, kind };
    };
    const onDockDragOver = (event: DragEvent): void => {
        const button = referenceDropButton(event);
        if (
            !button ||
            !hasDroppedFiles(event.dataTransfer) ||
            !dropTarget(button)
        ) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        setFileDropTarget(button);
        if (event.dataTransfer) {
            event.dataTransfer.dropEffect = "copy";
        }
    };
    const onDockDragLeave = (event: DragEvent): void => {
        const button = activeFileDropTarget;
        if (!button) {
            return;
        }
        const related = event.relatedTarget;
        if (related instanceof Node && button.contains(related)) {
            return;
        }
        if (event.target instanceof Node && button.contains(event.target)) {
            setFileDropTarget(null);
        }
    };
    const onDockDrop = (event: DragEvent): void => {
        const button = referenceDropButton(event);
        const target = button ? dropTarget(button) : null;
        const transfer = event.dataTransfer;
        if (!button || !transfer || !hasDroppedFiles(transfer) || !target) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        setFileDropTarget(null);
        void collectDroppedFiles(transfer).then(async (files) => {
            if (target.kind === "keyframe") {
                const uploads = await Promise.all(
                    files.map(async (file) => {
                        const data = await fileAsDataUri(file);
                        return data ? { data, fileName: file.name } : null;
                    }),
                );
                const added = selectionOperations.addRefEntries(
                    target.clipId,
                    uploads.flatMap((upload) => (upload ? [upload] : [])),
                );
                if (added < files.length) {
                    const skipped = files.length - added;
                    getVideoStagesHostBridge().showError(
                        `Added ${added} of ${files.length} keyframes. ` +
                            `${skipped} ${skipped === 1 ? "file" : "files"} could not be read or no supported keyframe position remained.`,
                    );
                }
                return;
            }
            return Promise.all(
                files.map(async (file) => ({
                    file,
                    media: await readDroppedReferenceMedia(file).catch(
                        () => null,
                    ),
                })),
            ).then((results) => {
                const references = results.flatMap(({ media }) =>
                    media ? [media] : [],
                );
                selectionOperations.addClipReferences(
                    target.clipId,
                    references,
                );
                const skipped = results
                    .filter(({ media }) => !media)
                    .map(({ file }) => file.name);
                if (skipped.length > 0) {
                    getVideoStagesHostBridge().showError(
                        `Unsupported reference files: ${skipped.join(", ")}`,
                    );
                }
            });
        });
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
                state.fps,
                snapshot.capabilities,
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
        const active = document.activeElement;
        revealSelectionOnNextRender = !(
            active instanceof HTMLElement && dockEl?.contains(active)
        );
        render();
    };

    const dispose = (): void => {
        draftQueue.dispose();
        closeTimelineAuthoringSettingsModal();
        closeTrimModal();
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
            releaseSidebarMediaPreviews(dockEl);
            dockEl.removeEventListener(
                "keydown",
                selectionOperations.onStripKeyDown,
            );
            dockEl.removeEventListener("focusout", focus.onDockFocusOut);
            dockEl.removeEventListener("focusin", focus.onDockFocusIn);
            dockEl.removeEventListener("change", focus.onDockChange);
            dockEl.removeEventListener("dragover", onDockDragOver);
            dockEl.removeEventListener("dragleave", onDockDragLeave);
            dockEl.removeEventListener("drop", onDockDrop);
            dockEl.className = DETAIL_CLASS;
            dockEl.innerHTML = "";
            dockEl = null;
        }
        setFileDropTarget(null);
        renderedSelection = null;
        renderEnabled = false;
        resetRememberedAccordionSections();
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
        dock.addEventListener("dragover", onDockDragOver);
        dock.addEventListener("dragleave", onDockDragLeave);
        dock.addEventListener("drop", onDockDrop);
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
