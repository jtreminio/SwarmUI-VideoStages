import type { TimelineSelection } from "../types";

interface FocusSnapshot {
    key: string;
    start: number | null;
    end: number | null;
}

export interface DetailFocusSession {
    isTypingInDock(): boolean;
    isSliderGesture(): boolean;
    capture(): void;
    restore(detail: HTMLElement): void;
    autoFocusSelection(detail: HTMLElement, selection: TimelineSelection): void;
    beginSelectionSession(): void;
    reset(): void;
    onDockFocusOut(event: FocusEvent): void;
    onDockFocusIn(): void;
    onDockChange(event: Event): void;
    onDocumentPointerDown(event: Event): void;
    onDocumentPointerUp(): void;
}

export const createDetailFocusSession = (options: {
    getDock: () => HTMLElement | null;
    isRendering: () => boolean;
    flushPending: () => void;
}): DetailFocusSession => {
    let sliderDragActive = false;
    let focusLeftDock = false;
    let pendingFocus: FocusSnapshot | null = null;

    const isTypingInDock = (): boolean => {
        const dock = options.getDock();
        const active = document.activeElement;
        if (
            !dock ||
            !(active instanceof HTMLElement) ||
            !dock.contains(active)
        ) {
            return false;
        }
        return (
            active instanceof HTMLTextAreaElement ||
            (active instanceof HTMLInputElement &&
                (active.type === "text" || active.type === "number"))
        );
    };

    const isSliderGesture = (): boolean => {
        if (sliderDragActive) {
            return true;
        }
        const dock = options.getDock();
        const active = document.activeElement;
        if (
            !dock ||
            !(active instanceof HTMLInputElement) ||
            !dock.contains(active)
        ) {
            return false;
        }
        return (
            active.type === "range" ||
            active.classList.contains("auto-slider-number")
        );
    };

    const capture = (): void => {
        const dock = options.getDock();
        if (focusLeftDock) {
            pendingFocus = null;
            return;
        }
        const active = document.activeElement;
        if (
            !dock ||
            !(active instanceof HTMLElement) ||
            !dock.contains(active)
        ) {
            pendingFocus = null;
            return;
        }
        const holder = active.closest("[data-vst-focus-key]");
        if (!holder || !dock.contains(holder)) {
            pendingFocus = null;
            return;
        }
        let start: number | null = null;
        let end: number | null = null;
        if (
            (active instanceof HTMLInputElement &&
                (active.type === "number" || active.type === "text")) ||
            active instanceof HTMLTextAreaElement
        ) {
            try {
                start = active.selectionStart;
                end = active.selectionEnd;
            } catch {}
        }
        pendingFocus = {
            key: holder.getAttribute("data-vst-focus-key") ?? "",
            start,
            end,
        };
    };

    const restore = (detail: HTMLElement): void => {
        const focus = pendingFocus;
        pendingFocus = null;
        if (!focus?.key) {
            return;
        }
        const holder = detail.querySelector<HTMLElement>(
            `[data-vst-focus-key="${focus.key}"]`,
        );
        if (!holder) {
            return;
        }
        holder.focus();
        if (
            (holder instanceof HTMLInputElement ||
                holder instanceof HTMLTextAreaElement) &&
            focus.start != null
        ) {
            try {
                holder.setSelectionRange(focus.start, focus.end ?? focus.start);
            } catch {}
        }
    };

    const focusKeyForSelection = (
        selection: TimelineSelection,
    ): string | null => {
        if (selection.kind === "prompt-major") {
            return "prompt-major";
        }
        if (selection.kind === "prompt-minor") {
            return `minor-${selection.windowIdx}`;
        }
        return null;
    };

    const autoFocusSelection = (
        detail: HTMLElement,
        selection: TimelineSelection,
    ): void => {
        if (focusLeftDock) {
            return;
        }
        const active = document.activeElement;
        if (active instanceof HTMLElement && detail.contains(active)) {
            return;
        }
        const focusKey = focusKeyForSelection(selection);
        if (!focusKey) {
            return;
        }
        const editor = detail.querySelector<HTMLTextAreaElement>(
            `textarea[data-vst-focus-key="${focusKey}"]`,
        );
        if (!editor) {
            return;
        }
        editor.focus();
        const length = editor.value.length;
        try {
            editor.setSelectionRange(length, length);
        } catch {}
        if (typeof editor.scrollIntoView === "function") {
            editor.scrollIntoView({ block: "nearest" });
        }
    };

    const onDockFocusOut = (event: FocusEvent): void => {
        if (options.isRendering()) {
            return;
        }
        const next = event.relatedTarget;
        const dock = options.getDock();
        if (next instanceof Node && dock?.contains(next)) {
            return;
        }
        focusLeftDock = true;
        pendingFocus = null;
        options.flushPending();
    };

    const onDockFocusIn = (): void => {
        focusLeftDock = false;
    };

    const onDockChange = (event: Event): void => {
        const target = event.target;
        if (
            target instanceof HTMLInputElement &&
            target.type === "number" &&
            document.activeElement === target
        ) {
            options.flushPending();
        }
    };

    const onDocumentPointerDown = (event: Event): void => {
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }
        if (!options.getDock()?.contains(target)) {
            options.flushPending();
            return;
        }
        if (target.closest('input[type="range"]')) {
            sliderDragActive = true;
        }
    };

    const onDocumentPointerUp = (): void => {
        if (!sliderDragActive) {
            return;
        }
        sliderDragActive = false;
        options.flushPending();
    };

    return {
        isTypingInDock,
        isSliderGesture,
        capture,
        restore,
        autoFocusSelection,
        beginSelectionSession: () => {
            pendingFocus = null;
            focusLeftDock = false;
        },
        reset: () => {
            sliderDragActive = false;
            focusLeftDock = false;
            pendingFocus = null;
        },
        onDockFocusOut,
        onDockFocusIn,
        onDockChange,
        onDocumentPointerDown,
        onDocumentPointerUp,
    };
};
