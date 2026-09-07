import { captureAuthoringTransactionSnapshot } from "../authoringSnapshot";
import { getSelection, setSelection } from "../selection";
import type { TimelineSelection } from "../types";
import type { DetailStripContext } from "./context";
import { createDetailDraftQueue, type DetailDraftQueue } from "./draftQueue";
import { createDetailFocusSession } from "./focusSession";
import { createDetailSelectionDomainOperations } from "./selectionDomainOperations";

export interface AuthoringSession {
    context: DetailStripContext;
    flush(): void;
    beforeRender(): void;
    afterRender(): void;
    dispose(): void;
}

export interface AuthoringSessionHost {
    render(): void;
    isRendering(): boolean;
    getRoot(): HTMLElement | null;
    setSelectionSilently?(selection: TimelineSelection): void;
}

export const createAuthoringSession = (
    host: AuthoringSessionHost,
): AuthoringSession => {
    let settingsMode: string | null = null;
    let boundRoot: HTMLElement | null = null;
    let draftQueue: DetailDraftQueue;
    const focus = createDetailFocusSession({
        getDock: host.getRoot,
        isRendering: host.isRendering,
        flushPending: () => draftQueue.flush(),
    });
    draftQueue = createDetailDraftQueue({
        focus,
        getDock: host.getRoot,
        isRendering: host.isRendering,
        getRenderedSelection: getSelection,
        syncValueDerivedUi: () => undefined,
        render: host.render,
        setSelectionSilently:
            host.setSelectionSilently ??
            ((selection) => setSelection(selection)),
    });
    const operations = createDetailSelectionDomainOperations(
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
        render: host.render,
        authoring: captureAuthoringTransactionSnapshot,
        ...operations,
        getBoundBody: host.getRoot,
        getSettingsMode: () => settingsMode,
        setSettingsMode: (mode) => {
            settingsMode = mode;
        },
    };

    const unbindRoot = (): void => {
        boundRoot?.removeEventListener("focusout", focus.onDockFocusOut);
        boundRoot?.removeEventListener("focusin", focus.onDockFocusIn);
        boundRoot?.removeEventListener("change", focus.onDockChange);
        boundRoot = null;
    };
    const bindRoot = (): void => {
        const root = host.getRoot();
        if (root === boundRoot) {
            return;
        }
        unbindRoot();
        boundRoot = root;
        boundRoot?.addEventListener("focusout", focus.onDockFocusOut);
        boundRoot?.addEventListener("focusin", focus.onDockFocusIn);
        boundRoot?.addEventListener("change", focus.onDockChange);
    };
    document.addEventListener("pointerdown", focus.onDocumentPointerDown, true);
    document.addEventListener("pointerup", focus.onDocumentPointerUp, true);
    document.addEventListener("pointercancel", focus.onDocumentPointerUp, true);

    return {
        context,
        flush: () => draftQueue.flush(),
        beforeRender: () => {
            bindRoot();
            draftQueue.flush();
            focus.capture();
            draftQueue.markCurrentSource();
        },
        afterRender: () => {
            const root = host.getRoot();
            if (root) {
                focus.restore(root);
            }
        },
        dispose: () => {
            draftQueue.dispose();
            focus.reset();
            unbindRoot();
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
        },
    };
};
