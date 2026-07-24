import { getVideoStagesHostBridge } from "./host";
import {
    getGroupToggle,
    getPromptInput,
    isVideoStagesEnabled,
} from "./swarmInputs";

const INPUT_SYNC_INTERVAL_MS = 200;

export interface TimelineHostLifecycle {
    bind(): void;
    dispose(): void;
}

export const createTimelineHostLifecycle = (options: {
    refresh: () => void;
    /** Re-requests the backend catalog after the host may have installed models. */
    refreshCatalog: () => void;
    syncFromCarrier: () => void;
    flushPending: () => void;
    undo: () => boolean;
    redo: () => boolean;
}): TimelineHostLifecycle => {
    let boundInput: HTMLInputElement | HTMLTextAreaElement | null = null;
    let boundToggle: HTMLInputElement | null = null;
    let inputSyncInterval: ReturnType<typeof setInterval> | null = null;
    let paramRefreshCleanup: (() => void) | null = null;

    const onInputChanged = (): void => options.syncFromCarrier();
    const onEnabledToggled = (): void => options.refresh();
    const onPageExit = (): void => options.flushPending();

    const bindInput = (): void => {
        const input = getPromptInput();
        if (!input || input === boundInput) return;
        boundInput?.removeEventListener("input", onInputChanged);
        boundInput?.removeEventListener("change", onInputChanged);
        input.addEventListener("input", onInputChanged);
        input.addEventListener("change", onInputChanged);
        boundInput = input;
    };

    const bindToggle = (): void => {
        const toggle = getGroupToggle();
        if (!toggle || toggle === boundToggle) return;
        boundToggle?.removeEventListener("change", onEnabledToggled);
        toggle.addEventListener("change", onEnabledToggled);
        boundToggle = toggle;
    };

    const onKeydown = (event: KeyboardEvent): void => {
        if (!(event.ctrlKey || event.metaKey)) return;
        const key = event.key.toLowerCase();
        const isUndo = key === "z" && !event.shiftKey;
        const isRedo = (key === "z" && event.shiftKey) || key === "y";
        if (!isUndo && !isRedo) return;
        const active = document.activeElement;
        const inTextField =
            active instanceof HTMLInputElement ||
            active instanceof HTMLTextAreaElement ||
            (active instanceof HTMLElement && active.isContentEditable);
        if (inTextField || !isVideoStagesEnabled()) return;
        if (isUndo ? options.undo() : options.redo()) event.preventDefault();
    };

    const bind = (): void => {
        bindInput();
        bindToggle();
        document.removeEventListener("keydown", onKeydown);
        document.addEventListener("keydown", onKeydown);
        window.removeEventListener("pagehide", onPageExit);
        window.addEventListener("pagehide", onPageExit);
        window.removeEventListener("beforeunload", onPageExit);
        window.addEventListener("beforeunload", onPageExit);
        if (!inputSyncInterval) {
            inputSyncInterval = setInterval(
                options.syncFromCarrier,
                INPUT_SYNC_INTERVAL_MS,
            );
        }
        if (!paramRefreshCleanup) {
            paramRefreshCleanup =
                getVideoStagesHostBridge().addParamRefreshHook(() => {
                    // A host model refresh can install models the cached
                    // catalog has never seen; without this they resolve to a
                    // null architecture until the page reloads.
                    options.refreshCatalog();
                    setTimeout(options.refresh, 0);
                });
        }
    };

    const dispose = (): void => {
        if (inputSyncInterval) {
            clearInterval(inputSyncInterval);
            inputSyncInterval = null;
        }
        boundInput?.removeEventListener("input", onInputChanged);
        boundInput?.removeEventListener("change", onInputChanged);
        boundInput = null;
        boundToggle?.removeEventListener("change", onEnabledToggled);
        boundToggle = null;
        paramRefreshCleanup?.();
        paramRefreshCleanup = null;
        document.removeEventListener("keydown", onKeydown);
        window.removeEventListener("pagehide", onPageExit);
        window.removeEventListener("beforeunload", onPageExit);
    };

    return { bind, dispose };
};
