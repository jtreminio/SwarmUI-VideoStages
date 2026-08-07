import { reconcileArchitectureIncomingIcLoraDrives } from "./architectures/behaviorRegistry";
import { documentFps } from "./documentQueries";
import type { GestureRouter, GestureSession } from "./gestureRouter";
import {
    dispatchDocumentCommand,
    getClips,
    getState,
    getTimelineStore,
    saveClips,
} from "./persistence/repository";
import { getRootDefaults } from "./rootDefaults";
import { getSelectedClipIndex, getSelection, setSelection } from "./selection";
import { getRootGeneratedEntryMode } from "./swarmInputs";
import { applyClipDurationResize, pxToDuration } from "./timelineEdit";
import {
    computeDropIndex,
    type DropRegion,
    finalIndexAfterMove,
    isNoOpMove,
    moveItem,
} from "./timelineReorder";
import { applySelectionHighlight } from "./timelineSelectionView";
import {
    commitClipMutation,
    currentRevision,
    livePxPerSecond,
    parseIntAttr,
} from "./trackDomUtils";

const REGION_SELECTOR = ".vst-region[data-clip-idx]";
const REGION_ACTION_SELECTOR = "[data-vst-region-action]";
const REGION_RESIZE_SELECTOR = ".vst-region-resize";
const CLIP_SHIFT_SELECTOR =
    ".vst-region[data-clip-idx], .vst-audio-clip[data-clip-idx]";

const DRAGGING_CLASS = "vst-dragging";
const RESIZING_CLASS = "vst-resizing";
const DROP_INDICATOR_CLASS = "vst-drop-indicator";

const DRAG_THRESHOLD_PX = 5;
const MIN_RESIZE_WIDTH_PX = 24;
const REGION_DRAGGING_CLASS = "vst-region-dragging";

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

const shiftClipsAfter = (
    body: HTMLElement,
    idx: number,
    deltaPx: number,
): void => {
    for (const el of body.querySelectorAll<HTMLElement>(CLIP_SHIFT_SELECTOR)) {
        const elIdx = parseIntAttr(el, "data-clip-idx");
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

export interface TimelineLinking {
    attach(body: HTMLElement, router: GestureRouter): void;
    reapplySelection(body: HTMLElement, clipCount: number): void;
    getSelectedIndex(): number | null;
    dispose(): void;
}

export const createTimelineLinking = (): TimelineLinking => {
    let attachedBody: HTMLElement | null = null;

    // Any clip-bound selection kind, or null. Backed by shared uiState selection.
    const selectedClip = (): number | null => getSelectedClipIndex();

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

    const onRegionClick = (body: HTMLElement, event: Event): void => {
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }
        const actionButton = target.closest(REGION_ACTION_SELECTOR);
        if (actionButton) {
            event.stopPropagation();
            const actionRegion = actionButton.closest(REGION_SELECTOR);
            const actionIdx = parseIntAttr(actionRegion, "data-clip-idx");
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
        const idx = parseIntAttr(region, "data-clip-idx");
        if (idx === null) {
            return;
        }
        if ((event as MouseEvent).shiftKey) {
            applyDelete(idx);
            return;
        }
        selectClip(idx, 0);
        applySelectionHighlight(body);
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
        const snapshot = getTimelineStore().getSnapshot();
        const clip = snapshot.state.clips[idx];
        if (!clip?.id || (idx === 0 && clip.skipped !== true)) {
            return;
        }
        dispatchDocumentCommand(
            { type: "clip.toggle-skip", clipId: clip.id },
            {
                origin: "linking",
                expectedRevision: snapshot.revision,
            },
        );
    };

    const applyDelete = (idx: number): void => {
        const clips = getClips();
        if (idx <= 0 || idx >= clips.length) {
            return;
        }
        clips.splice(idx, 1);
        reconcileArchitectureIncomingIcLoraDrives(
            clips,
            getRootGeneratedEntryMode(),
            getRootDefaults().modelCatalog,
        );
        // No selection remap here: selection is anchored to entity ids, so a
        // surviving selection follows its clip and a deleted one degrades to
        // the nearest surviving sibling on the next read.
        saveClips(clips, { origin: "linking" });
    };

    interface ResizeState {
        idx: number;
        el: HTMLElement;
        startLeftPx: number;
        originalWidthPx: number;
        sourceRevision: number;
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
            suppressEscapeClick: true,
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
                const committed = commitClipMutation(
                    state.sourceRevision,
                    "linking",
                    (clips) => {
                        const clip = clips[state.idx];
                        if (
                            state.idx < 0 ||
                            state.idx >= clips.length ||
                            clip.clipLengthFromAudio ||
                            clip.clipLengthFromControlNet ||
                            clip.initVideo
                        ) {
                            return null;
                        }
                        const fps = documentFps(getState());
                        const pxPerSecond = livePxPerSecond(body);
                        const joinTrimSeconds = Math.max(
                            0,
                            Number(state.el.dataset.vstJoinTrimSeconds ?? 0) ||
                                0,
                        );
                        const newDuration = pxToDuration(
                            width + joinTrimSeconds * pxPerSecond,
                            pxPerSecond,
                            fps,
                        );
                        if (
                            !applyClipDurationResize(
                                clip,
                                newDuration,
                                getRootDefaults(),
                                fps,
                            )
                        ) {
                            return null;
                        }
                        selectClip(state.idx, stageForClip(state.idx));
                        return clips;
                    },
                );
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
        sourceRevision: number;
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
            suppressEscapeClick: true,
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
                    applySelectionHighlight(body);
                    return;
                }
                const stageIdx = stageForClip(from);
                const moved = commitClipMutation(
                    state.sourceRevision,
                    "linking",
                    (clips) => {
                        if (from < 0 || from >= clips.length) {
                            return null;
                        }
                        const reordered = moveItem(clips, from, gap);
                        reconcileArchitectureIncomingIcLoraDrives(
                            reordered,
                            getRootGeneratedEntryMode(),
                            getRootDefaults().modelCatalog,
                        );
                        return reordered;
                    },
                );
                if (moved) {
                    // After the save: the selection anchors to the clip that is
                    // really at the destination, not to whoever held that slot
                    // before the move.
                    selectClip(finalIndexAfterMove(from, gap), stageIdx);
                }
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
            const idx = parseIntAttr(region, "data-clip-idx");
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
                sourceRevision: currentRevision(),
            });
        }
        const target = me.target.closest(REGION_SELECTOR);
        const idx = parseIntAttr(target, "data-clip-idx");
        if (idx === null) {
            return null;
        }
        return dragSession(body, {
            sourceIdx: idx,
            sourceRevision: currentRevision(),
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
        applySelectionHighlight(body);
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
