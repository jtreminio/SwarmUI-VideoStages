import type { GestureRouter, GestureSession } from "./gestureRouter";
import { getClips, saveClips } from "./persistence";
import { getRootDefaults } from "./rootDefaults";
import { readStateToken } from "./swarmInputs";
import { applyClipDurationResize, pxToDuration } from "./timelineEdit";
import {
    computeDropIndex,
    type DropRegion,
    finalIndexAfterMove,
    isNoOpMove,
    moveItem,
} from "./timelineReorder";
import { DEFAULT_PX_PER_SECOND } from "./timelineView";
import { getSelectedClipIndex, getSelection, setSelection } from "./uiState";

const REGION_SELECTOR = ".vst-region[data-clip-idx]";
const REGION_ACTION_SELECTOR = "[data-vst-region-action]";
const REGION_RESIZE_SELECTOR = ".vst-region-resize";
const CLIP_SHIFT_SELECTOR =
    ".vst-region[data-clip-idx], .vst-audio-clip[data-clip-idx]";

const REGION_SELECTED_CLASS = "vst-region-selected";
const DRAGGING_CLASS = "vst-dragging";
const RESIZING_CLASS = "vst-resizing";
const DROP_INDICATOR_CLASS = "vst-drop-indicator";

const DRAG_THRESHOLD_PX = 5;
const MIN_RESIZE_WIDTH_PX = 24;
const REGION_DRAGGING_CLASS = "vst-region-dragging";

export const livePxPerSecond = (body: HTMLElement): number => {
    const pps = Number.parseFloat(body.dataset.vstPps ?? "");
    return Number.isFinite(pps) && pps > 0 ? pps : DEFAULT_PX_PER_SECOND;
};

const currentFps = (): number => {
    try {
        const fps = getRootDefaults().fps;
        return typeof fps === "number" && fps > 0 ? fps : 24;
    } catch {
        return 24;
    }
};

export const resolveSelectedIndex = (
    selectedIndex: number | null,
    clipCount: number,
): number | null => {
    if (
        selectedIndex === null ||
        !Number.isInteger(selectedIndex) ||
        selectedIndex < 0 ||
        selectedIndex >= clipCount
    ) {
        return null;
    }
    return selectedIndex;
};

export const parseClipIdx = (el: Element | null): number | null => {
    if (!el) {
        return null;
    }
    const raw = el.getAttribute("data-clip-idx");
    if (raw === null) {
        return null;
    }
    const idx = Number.parseInt(raw, 10);
    return Number.isInteger(idx) && idx >= 0 ? idx : null;
};

const shiftClipsAfter = (
    body: HTMLElement,
    idx: number,
    deltaPx: number,
): void => {
    for (const el of body.querySelectorAll<HTMLElement>(CLIP_SHIFT_SELECTOR)) {
        const elIdx = parseClipIdx(el);
        if (elIdx !== null && elIdx > idx) {
            el.style.transform =
                deltaPx !== 0 ? `translateX(${deltaPx}px)` : "";
        }
    }
};

const clearClipShifts = (body: HTMLElement): void => {
    for (const el of body.querySelectorAll<HTMLElement>(CLIP_SHIFT_SELECTOR)) {
        el.style.transform = "";
    }
};

export const parseRefIdx = (el: Element | null): number | null => {
    if (!el) {
        return null;
    }
    const raw = el.getAttribute("data-ref-idx");
    if (raw === null) {
        return null;
    }
    const idx = Number.parseInt(raw, 10);
    return Number.isInteger(idx) && idx >= 0 ? idx : null;
};

export interface TimelineLinking {
    attach(body: HTMLElement, router: GestureRouter): void;
    reapplySelection(body: HTMLElement, clipCount: number): void;
    getSelectedIndex(): number | null;
    dispose(): void;
}

export const createTimelineLinking = (): TimelineLinking => {
    let attachedBody: HTMLElement | null = null;

    // The clip index for the currently selected clip (any clip-bound
    // selection kind), or null. Backed by the shared uiState selection.
    const selectedClip = (): number | null => getSelectedClipIndex();

    // Stage index to keep when re-selecting a clip we already have open.
    const stageForClip = (clipIdx: number): number => {
        const sel = getSelection();
        return sel.kind === "clip" && sel.clipIdx === clipIdx
            ? sel.stageIdx
            : 0;
    };

    const selectClip = (clipIdx: number, stageIdx: number): void => {
        setSelection({ kind: "clip", clipIdx, stageIdx });
    };

    let dropIndicator: HTMLElement | null = null;

    const findRegion = (body: HTMLElement, idx: number): HTMLElement | null =>
        body.querySelector<HTMLElement>(`.vst-region[data-clip-idx="${idx}"]`);

    const markSelection = (body: HTMLElement): void => {
        for (const region of body.querySelectorAll(
            `.${REGION_SELECTED_CLASS}`,
        )) {
            region.classList.remove(REGION_SELECTED_CLASS);
        }
        const idx = selectedClip();
        if (idx === null) {
            return;
        }
        findRegion(body, idx)?.classList.add(REGION_SELECTED_CLASS);
    };

    const onRegionClick = (body: HTMLElement, event: Event): void => {
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }
        const actionButton = target.closest(REGION_ACTION_SELECTOR);
        if (actionButton) {
            event.stopPropagation();
            const actionRegion = actionButton.closest(REGION_SELECTOR);
            const actionIdx = parseClipIdx(actionRegion);
            if (actionIdx === null) {
                return;
            }
            const action = actionButton.getAttribute("data-vst-region-action");
            if (action === "skip") {
                applySkip(actionIdx);
            }
            return;
        }
        const region = target.closest(REGION_SELECTOR);
        const idx = parseClipIdx(region);
        if (idx === null) {
            return;
        }
        if ((event as MouseEvent).shiftKey) {
            applyDelete(idx);
            return;
        }
        selectClip(idx, 0);
        markSelection(body);
    };

    const readRegions = (
        body: HTMLElement,
    ): { els: HTMLElement[]; rects: DropRegion[] } => {
        const els = Array.from(
            body.querySelectorAll<HTMLElement>(REGION_SELECTOR),
        );
        const rects = els.map((el) => {
            const r = el.getBoundingClientRect();
            return { startPx: r.left, widthPx: r.width };
        });
        return { els, rects };
    };

    const removeDropIndicator = (): void => {
        dropIndicator?.remove();
        dropIndicator = null;
    };

    const showDropIndicator = (els: HTMLElement[], gap: number): void => {
        if (els.length === 0) {
            return;
        }
        const track = els[0].parentElement;
        if (!track) {
            return;
        }
        if (!dropIndicator) {
            dropIndicator = document.createElement("div");
            dropIndicator.className = DROP_INDICATOR_CLASS;
        }
        if (dropIndicator.parentElement !== track) {
            track.appendChild(dropIndicator);
        }
        const left =
            gap < els.length
                ? els[gap].offsetLeft
                : els[els.length - 1].offsetLeft +
                  els[els.length - 1].offsetWidth;
        dropIndicator.style.left = `${left}px`;
    };

    const applySkip = (idx: number): void => {
        const clips = getClips();
        if (idx < 0 || idx >= clips.length) {
            return;
        }
        clips[idx].skipped = !clips[idx].skipped;
        saveClips(clips, undefined, { origin: "linking" });
    };

    const applyDelete = (idx: number): void => {
        const clips = getClips();
        if (idx < 0 || idx >= clips.length) {
            return;
        }
        clips.splice(idx, 1);
        const sel = getSelection();
        if (sel.kind === "clip") {
            if (sel.clipIdx === idx) {
                setSelection({ kind: "none" });
            } else if (sel.clipIdx > idx) {
                setSelection({ ...sel, clipIdx: sel.clipIdx - 1 });
            }
        }
        saveClips(clips, undefined, { origin: "linking" });
    };

    interface ResizeState {
        idx: number;
        el: HTMLElement;
        startLeftPx: number;
        originalWidthPx: number;
        sourceJson: string;
    }

    const resizeSession = (
        body: HTMLElement,
        state: ResizeState,
    ): GestureSession => {
        const restore = (): void => {
            state.el.style.width = `${state.originalWidthPx}px`;
            clearClipShifts(body);
            body.classList.remove(RESIZING_CLASS);
        };
        return {
            threshold: DRAG_THRESHOLD_PX,
            escapeClickSuppression: "if-active",
            onMove: (ctx) => {
                const width = Math.max(
                    MIN_RESIZE_WIDTH_PX,
                    ctx.event.clientX - state.startLeftPx,
                );
                body.classList.add(RESIZING_CLASS);
                state.el.style.width = `${width}px`;
                shiftClipsAfter(body, state.idx, width - state.originalWidthPx);
            },
            onCommit: (ctx) => {
                const width = ctx.event.clientX - state.startLeftPx;
                let committed = false;
                if (readStateToken() === state.sourceJson) {
                    const clips = getClips();
                    if (
                        state.idx >= 0 &&
                        state.idx < clips.length &&
                        !clips[state.idx].clipLengthFromAudio &&
                        !clips[state.idx].clipLengthFromControlNet
                    ) {
                        const newDuration = pxToDuration(
                            width,
                            livePxPerSecond(body),
                            currentFps(),
                        );
                        if (
                            applyClipDurationResize(
                                clips[state.idx],
                                newDuration,
                                getRootDefaults,
                            )
                        ) {
                            selectClip(state.idx, stageForClip(state.idx));
                            saveClips(clips, undefined, { origin: "linking" });
                            committed = true;
                        }
                    }
                }
                if (committed) {
                    // Keep the preview width/shifts; the save's re-render
                    // replaces them with the real layout.
                    body.classList.remove(RESIZING_CLASS);
                } else {
                    restore();
                }
            },
            onTap: restore,
            onCancel: restore,
        };
    };

    interface DragState {
        sourceIdx: number;
        sourceJson: string;
    }

    const dragSession = (
        body: HTMLElement,
        state: DragState,
    ): GestureSession => {
        const cleanup = (): void => {
            findRegion(body, state.sourceIdx)?.classList.remove(
                REGION_DRAGGING_CLASS,
            );
            removeDropIndicator();
            body.classList.remove(DRAGGING_CLASS);
        };
        return {
            threshold: DRAG_THRESHOLD_PX,
            axis: "xy",
            escapeClickSuppression: "if-active",
            onMove: (ctx) => {
                body.classList.add(DRAGGING_CLASS);
                findRegion(body, state.sourceIdx)?.classList.add(
                    REGION_DRAGGING_CLASS,
                );
                const { els, rects } = readRegions(body);
                showDropIndicator(
                    els,
                    computeDropIndex(ctx.event.clientX, rects),
                );
            },
            onCommit: (ctx) => {
                cleanup();
                const { rects } = readRegions(body);
                const gap = computeDropIndex(ctx.event.clientX, rects);
                const from = state.sourceIdx;
                if (isNoOpMove(from, gap)) {
                    selectClip(from, stageForClip(from));
                    markSelection(body);
                    return;
                }
                if (readStateToken() !== state.sourceJson) {
                    return;
                }
                const clips = getClips();
                if (from < 0 || from >= clips.length) {
                    return;
                }
                const destIdx = finalIndexAfterMove(from, gap);
                selectClip(destIdx, stageForClip(from));
                saveClips(moveItem(clips, from, gap), undefined, {
                    origin: "linking",
                });
            },
            onCancel: cleanup,
        };
    };

    const onPress = (
        me: MouseEvent,
        body: HTMLElement,
    ): GestureSession | null => {
        if (!(me.target instanceof Element)) {
            return null;
        }
        if (me.target.closest(REGION_ACTION_SELECTOR)) {
            return null;
        }
        if (me.shiftKey) {
            // No drag on a shift press; the shift-CLICK delete is handled by
            // onRegionClick.
            me.preventDefault();
            return null;
        }
        const resizeGrip = me.target.closest(REGION_RESIZE_SELECTOR);
        if (resizeGrip) {
            const region = resizeGrip.closest(REGION_SELECTOR);
            const idx = parseClipIdx(region);
            if (idx === null || !(region instanceof HTMLElement)) {
                return null;
            }
            const rect = region.getBoundingClientRect();
            me.preventDefault();
            return resizeSession(body, {
                idx,
                el: region,
                startLeftPx: rect.left,
                originalWidthPx: rect.width,
                sourceJson: readStateToken(),
            });
        }
        const target = me.target.closest(REGION_SELECTOR);
        const idx = parseClipIdx(target);
        if (idx === null) {
            return null;
        }
        return dragSession(body, {
            sourceIdx: idx,
            sourceJson: readStateToken(),
        });
    };

    let bodyClickHandler: ((e: Event) => void) | null = null;
    let unregister: (() => void) | null = null;

    const attach = (body: HTMLElement, router: GestureRouter): void => {
        if (attachedBody === body) {
            return;
        }
        if (attachedBody) {
            dispose();
        }
        bodyClickHandler = (e) => onRegionClick(body, e);
        body.addEventListener("click", bodyClickHandler);
        unregister = router.register({
            id: "linking",
            priority: 10,
            onPress,
        });
        attachedBody = body;
    };

    const reapplySelection = (body: HTMLElement, clipCount: number): void => {
        const idx = selectedClip();
        if (idx !== null && resolveSelectedIndex(idx, clipCount) === null) {
            setSelection({ kind: "none" });
        }
        markSelection(body);
    };

    const dispose = (): void => {
        if (attachedBody) {
            if (bodyClickHandler) {
                attachedBody.removeEventListener("click", bodyClickHandler);
            }
        }
        removeDropIndicator();
        unregister?.();
        unregister = null;
        bodyClickHandler = null;
        attachedBody = null;
    };

    const getSelectedIndex = (): number | null => selectedClip();

    return { attach, reapplySelection, getSelectedIndex, dispose };
};
