import type { DetailStripContext } from "./detailStrip/context";
import {
    createDetailDraftQueue,
    type DetailDraftQueue,
} from "./detailStrip/draftQueue";
import { createDetailFocusSession } from "./detailStrip/focusSession";
import { clampDetailSelection } from "./detailStrip/panelRouter";
import {
    createPanelSelectionSession,
    isRenderedSelection,
} from "./detailStrip/panelSelectionSession";
import { renderDetailShell } from "./detailStrip/renderShell";
import { createDetailSelectionOperations } from "./detailStrip/selectionOperations";
import { getClips } from "./persistence";
import { getRootDefaults } from "./rootDefaults";
import {
    getSelection,
    isSameSelection,
    setSelection,
    subscribeSelection,
} from "./selection";
import type { UpdateMeta } from "./store";
import { getRootGeneratedEntryMode } from "./swarmInputs";
import type { TimelineSelection } from "./types";

const DETAIL_CLASS = "vst-detail";

export interface TimelineDetailStrip {
    /**
     * @param body listener-host carrying capture-phase timeline chip listeners.
     * @param dock render-host owned by the caller.
     */
    attach(body: HTMLElement, dock: HTMLElement): void;
    render(meta?: UpdateMeta): void;
    dispose(): void;
}

export interface TimelineDetailStripOptions {
    isCollapsed: () => boolean;
    setCollapsed: (collapsed: boolean) => void;
}

export const createTimelineDetailStrip = (
    options: TimelineDetailStripOptions,
): TimelineDetailStrip => {
    let boundBody: HTMLElement | null = null;
    let dockEl: HTMLElement | null = null;
    let unsubscribe: (() => void) | null = null;
    let rendering = false;
    let suppressSelectionRender = false;
    let settingsMode: string | null = null;
    let draftQueue: DetailDraftQueue;
    let renderImplementation: (meta?: UpdateMeta) => void = () => {};
    const render = (meta?: UpdateMeta): void => renderImplementation(meta);

    const panelSelection = createPanelSelectionSession();
    const focus = createDetailFocusSession({
        getDock: () => dockEl,
        isRendering: () => rendering,
        flushPending: () => draftQueue?.flush(),
    });
    const syncValueDerivedUi = (selection: TimelineSelection | null): void => {
        if (!selection) {
            return;
        }
        panelSelection.syncBreadcrumb(dockEl, getClips());
    };
    draftQueue = createDetailDraftQueue({
        focus,
        getDock: () => dockEl,
        isRendering: () => rendering,
        getRenderedSelection: panelSelection.getRendered,
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
        () => createCapabilityViewResolver(getRootDefaults().modelCatalog),
        render,
        getRootGeneratedEntryMode,
    );

    const context: DetailStripContext = {
        commit: draftQueue.commit,
        commitState: draftQueue.commitState,
        debouncedCommit: draftQueue.debouncedCommit,
        debouncedCommitState: draftQueue.debouncedCommitState,
        buildClampedNumber: draftQueue.buildClampedNumber,
        structuralCommit: draftQueue.structuralCommit,
        render,
        capabilities: () =>
            createCapabilityViewResolver(getRootDefaults().modelCatalog),
        generatedEntryMode: getRootGeneratedEntryMode,
        deleteRefEntry: selectionOperations.deleteRefEntry,
        deleteWindowEntry: selectionOperations.deleteWindowEntry,
        createRetake: selectionOperations.createRetake,
        removeRetake: selectionOperations.removeRetake,
        addAudioSegment: selectionOperations.addAudioSegment,
        removeAudioSegment: selectionOperations.removeAudioSegment,
        addStage: selectionOperations.addStage,
        deleteStage: selectionOperations.deleteStage,
        selectStage: selectionOperations.selectStage,
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

    renderImplementation = (meta?: UpdateMeta): void => {
        if (!dockEl) {
            return;
        }
        const renderedSelection = panelSelection.getRendered();
        if (
            meta?.origin === "detail-strip" &&
            meta.hint === "value-only" &&
            renderedSelection &&
            !options.isCollapsed() &&
            isRenderedSelection(panelSelection, getSelection())
        ) {
            draftQueue.markCurrentSource();
            syncValueDerivedUi(renderedSelection);
            return;
        }

        // Load-bearing: pending field edits flush before widget teardown or
        // synthetic slider setup can observe stale carrier state.
        draftQueue.flush();
        rendering = true;
        try {
            draftQueue.markCurrentSource();
            const detail = ensureDetail();
            const clips = getClips();
            const rawSelection = getSelection();
            const selection = clampDetailSelection(rawSelection, clips);
            if (!isSameSelection(rawSelection, selection)) {
                setSelection(selection);
                return;
            }

            const collapsed = options.isCollapsed();
            renderDetailShell({
                detail,
                context,
                focus,
                clips,
                selection,
                previousSelection: renderedSelection,
                collapsed,
                clearSelection: () => setSelection({ kind: "none" }),
                toggleCollapsed: () => {
                    options.setCollapsed(!options.isCollapsed());
                    render();
                },
            });
            panelSelection.setRendered(selection);
        } finally {
            rendering = false;
        }
    };

    const onSelectionChanged = (selection: TimelineSelection): void => {
        if (suppressSelectionRender) {
            return;
        }
        if (
            panelSelection.targetedReselect(
                selection,
                dockEl,
                options.isCollapsed(),
                getClips(),
            )
        ) {
            return;
        }
        focus.beginSelectionSession();
        settingsMode = null;
        if (selection.kind !== "none" && options.isCollapsed()) {
            options.setCollapsed(false);
        }
        render();
    };

    const dispose = (): void => {
        draftQueue.dispose();
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
        panelSelection.clear();
    };

    const attach = (body: HTMLElement, dock: HTMLElement): void => {
        if (boundBody === body && dockEl === dock) {
            return;
        }
        dispose();
        boundBody = body;
        dockEl = dock;
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
        render();
    };

    return { attach, render, dispose };
};

import { createCapabilityViewResolver } from "./architectures/policy";
