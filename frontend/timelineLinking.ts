import { clamp, REF_FRAME_MIN } from "./constants";
import { getReferenceFrameMax } from "./normalization";
import { getClips, saveClips } from "./persistence";
import { getRootDefaults } from "./rootDefaults";
import { readStateToken } from "./swarmInputs";
import { keyframeLeftPercent, keyframeTimeSeconds } from "./timelineDetail";
import {
    applyClipDurationResize,
    pxToDuration,
    pxToFrame,
} from "./timelineEdit";
import {
    computeDropIndex,
    type DropRegion,
    finalIndexAfterMove,
    isNoOpMove,
    moveItem,
} from "./timelineReorder";
import { DEFAULT_PX_PER_SECOND } from "./timelineView";

const REGION_SELECTOR = ".vst-region[data-clip-idx]";
const REGION_ACTION_SELECTOR = "[data-vst-region-action]";
const REGION_RESIZE_SELECTOR = ".vst-region-resize";
const CLIP_SHIFT_SELECTOR =
    ".vst-region[data-clip-idx], .vst-audio-clip[data-clip-idx]";
const KEY_SELECTOR = ".vst-key[data-ref-idx]";

const REGION_SELECTED_CLASS = "vst-region-selected";
const DRAGGING_CLASS = "vst-dragging";
const RESIZING_CLASS = "vst-resizing";
const KEYFRAMING_CLASS = "vst-keyframing";
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
    attach(body: HTMLElement): void;
    reapplySelection(body: HTMLElement, clipCount: number): void;
    getSelectedIndex(): number | null;
    dispose(): void;
}

export const createTimelineLinking = (): TimelineLinking => {
    let attachedBody: HTMLElement | null = null;
    let selectedIndex: number | null = null;

    let dragState: {
        sourceIdx: number;
        startX: number;
        startY: number;
        active: boolean;
        sourceJson: string;
    } | null = null;
    let suppressClick = false;
    let dropIndicator: HTMLElement | null = null;

    let resizeState: {
        idx: number;
        el: HTMLElement;
        startX: number;
        startLeftPx: number;
        originalWidthPx: number;
        active: boolean;
        sourceJson: string;
    } | null = null;

    let keyframeState: {
        clipIdx: number;
        refIdx: number;
        el: HTMLElement;
        regionEl: HTMLElement;
        startX: number;
        originalLeft: string;
        active: boolean;
        durationSeconds: number;
        fps: number;
        fromEnd: boolean;
        shiftKey: boolean;
        sourceJson: string;
    } | null = null;

    const findRegion = (body: HTMLElement, idx: number): HTMLElement | null =>
        body.querySelector<HTMLElement>(`.vst-region[data-clip-idx="${idx}"]`);

    const markSelection = (body: HTMLElement): void => {
        for (const region of body.querySelectorAll(
            `.${REGION_SELECTED_CLASS}`,
        )) {
            region.classList.remove(REGION_SELECTED_CLASS);
        }
        if (selectedIndex === null) {
            return;
        }
        findRegion(body, selectedIndex)?.classList.add(REGION_SELECTED_CLASS);
    };

    const onRegionClick = (body: HTMLElement, event: Event): void => {
        if (suppressClick) {
            suppressClick = false;
            return;
        }
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
        selectedIndex = idx;
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

    const endDrag = (body: HTMLElement): void => {
        if (dragState) {
            findRegion(body, dragState.sourceIdx)?.classList.remove(
                REGION_DRAGGING_CLASS,
            );
        }
        dragState = null;
        removeDropIndicator();
        body.classList.remove(DRAGGING_CLASS);
    };

    const endResize = (body: HTMLElement): void => {
        if (resizeState) {
            resizeState.el.style.width = `${resizeState.originalWidthPx}px`;
        }
        clearClipShifts(body);
        resizeState = null;
        body.classList.remove(RESIZING_CLASS);
    };

    const applySkip = (idx: number): void => {
        const clips = getClips();
        if (idx < 0 || idx >= clips.length) {
            return;
        }
        clips[idx].skipped = !clips[idx].skipped;
        saveClips(clips);
    };

    const applyDelete = (idx: number): void => {
        const clips = getClips();
        if (idx < 0 || idx >= clips.length) {
            return;
        }
        clips.splice(idx, 1);
        if (selectedIndex !== null) {
            if (selectedIndex === idx) {
                selectedIndex = null;
            } else if (selectedIndex > idx) {
                selectedIndex -= 1;
            }
        }
        saveClips(clips);
    };

    const applyToggleKeyframeFromEnd = (
        clipIdx: number,
        refIdx: number,
        sourceJson: string,
    ): void => {
        if (readStateToken() !== sourceJson) {
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        const ref = clip?.refs?.[refIdx];
        if (!ref) {
            return;
        }
        ref.fromEnd = !ref.fromEnd;
        ref.frame = clamp(
            ref.frame,
            REF_FRAME_MIN,
            getReferenceFrameMax(getRootDefaults, clip),
        );
        saveClips(clips);
    };

    const endKeyframe = (body: HTMLElement): void => {
        if (keyframeState) {
            keyframeState.el.style.left = keyframeState.originalLeft;
        }
        keyframeState = null;
        body.classList.remove(KEYFRAMING_CLASS);
    };

    const onBodyMouseDown = (event: Event): void => {
        suppressClick = false;
        const me = event as MouseEvent;
        if (me.button !== 0) {
            return;
        }
        if (!(me.target instanceof Element)) {
            return;
        }
        const pip = me.target.closest(KEY_SELECTOR);
        if (pip instanceof HTMLElement) {
            const pipRegion = pip.closest(REGION_SELECTOR);
            const clipIdx = parseClipIdx(pipRegion);
            const refIdx = parseRefIdx(pip);
            if (
                clipIdx === null ||
                refIdx === null ||
                !(pipRegion instanceof HTMLElement)
            ) {
                return;
            }
            const clips = getClips();
            const clip = clips[clipIdx];
            const ref = clip?.refs?.[refIdx];
            if (!ref) {
                return;
            }
            keyframeState = {
                clipIdx,
                refIdx,
                el: pip,
                regionEl: pipRegion,
                startX: me.clientX,
                originalLeft: pip.style.left,
                active: false,
                durationSeconds: clip.duration,
                fps: currentFps(),
                fromEnd: ref.fromEnd === true,
                shiftKey: me.shiftKey,
                sourceJson: readStateToken(),
            };
            me.preventDefault();
            return;
        }
        if (me.target.closest(REGION_ACTION_SELECTOR)) {
            return;
        }
        if (me.shiftKey) {
            me.preventDefault();
            return;
        }
        const resizeGrip = me.target.closest(REGION_RESIZE_SELECTOR);
        if (resizeGrip) {
            const region = resizeGrip.closest(REGION_SELECTOR);
            const idx = parseClipIdx(region);
            if (idx === null || !(region instanceof HTMLElement)) {
                return;
            }
            const rect = region.getBoundingClientRect();
            resizeState = {
                idx,
                el: region,
                startX: me.clientX,
                startLeftPx: rect.left,
                originalWidthPx: rect.width,
                active: false,
                sourceJson: readStateToken(),
            };
            me.preventDefault();
            return;
        }
        const target = me.target.closest(REGION_SELECTOR);
        const idx = parseClipIdx(target);
        if (idx === null) {
            return;
        }
        dragState = {
            sourceIdx: idx,
            startX: me.clientX,
            startY: me.clientY,
            active: false,
            sourceJson: readStateToken(),
        };
    };

    const onDocMouseMove = (body: HTMLElement, event: Event): void => {
        if (keyframeState) {
            const kme = event as MouseEvent;
            if (!keyframeState.active) {
                if (
                    Math.abs(kme.clientX - keyframeState.startX) <
                    DRAG_THRESHOLD_PX
                ) {
                    return;
                }
                keyframeState.active = true;
                body.classList.add(KEYFRAMING_CLASS);
            }
            const rect = keyframeState.regionEl.getBoundingClientRect();
            const frame = pxToFrame(
                kme.clientX - rect.left,
                rect.width,
                keyframeState.durationSeconds,
                keyframeState.fps,
                keyframeState.fromEnd,
            );
            const time = keyframeTimeSeconds(
                frame,
                keyframeState.fromEnd,
                keyframeState.durationSeconds,
                keyframeState.fps,
            );
            keyframeState.el.style.left = `${keyframeLeftPercent(
                time,
                keyframeState.durationSeconds,
            )}%`;
            return;
        }
        if (resizeState) {
            const rme = event as MouseEvent;
            if (!resizeState.active) {
                if (
                    Math.abs(rme.clientX - resizeState.startX) <
                    DRAG_THRESHOLD_PX
                ) {
                    return;
                }
                resizeState.active = true;
            }
            const width = Math.max(
                MIN_RESIZE_WIDTH_PX,
                rme.clientX - resizeState.startLeftPx,
            );
            body.classList.add(RESIZING_CLASS);
            resizeState.el.style.width = `${width}px`;
            shiftClipsAfter(
                body,
                resizeState.idx,
                width - resizeState.originalWidthPx,
            );
            return;
        }
        if (!dragState) {
            return;
        }
        const me = event as MouseEvent;
        if (!dragState.active) {
            const dx = me.clientX - dragState.startX;
            const dy = me.clientY - dragState.startY;
            if (Math.hypot(dx, dy) < DRAG_THRESHOLD_PX) {
                return;
            }
            dragState.active = true;
            body.classList.add(DRAGGING_CLASS);
            findRegion(body, dragState.sourceIdx)?.classList.add(
                REGION_DRAGGING_CLASS,
            );
        }
        const { els, rects } = readRegions(body);
        showDropIndicator(els, computeDropIndex(me.clientX, rects));
    };

    const onDocMouseUp = (body: HTMLElement, event: Event): void => {
        if (keyframeState) {
            const ks = keyframeState;
            const kme = event as MouseEvent;
            const rect = ks.regionEl.getBoundingClientRect();
            endKeyframe(body);
            suppressClick = true;
            if (!ks.active) {
                if (ks.shiftKey) {
                    applyToggleKeyframeFromEnd(
                        ks.clipIdx,
                        ks.refIdx,
                        ks.sourceJson,
                    );
                }
                return;
            }
            if (readStateToken() !== ks.sourceJson) {
                return;
            }
            const newFrame = pxToFrame(
                kme.clientX - rect.left,
                rect.width,
                ks.durationSeconds,
                ks.fps,
                ks.fromEnd,
            );
            const clips = getClips();
            const ref = clips[ks.clipIdx]?.refs?.[ks.refIdx];
            if (!ref || ref.frame === newFrame) {
                return;
            }
            ref.frame = newFrame;
            saveClips(clips);
            return;
        }
        if (resizeState) {
            const rs = resizeState;
            const me = event as MouseEvent;
            endResize(body);
            if (!rs.active) {
                return;
            }
            const width = me.clientX - rs.startLeftPx;
            suppressClick = true;
            if (readStateToken() !== rs.sourceJson) {
                return;
            }
            const clips = getClips();
            if (rs.idx < 0 || rs.idx >= clips.length) {
                return;
            }
            if (
                clips[rs.idx].clipLengthFromAudio ||
                clips[rs.idx].clipLengthFromControlNet
            ) {
                return;
            }
            const newDuration = pxToDuration(
                width,
                livePxPerSecond(body),
                currentFps(),
            );
            if (
                applyClipDurationResize(
                    clips[rs.idx],
                    newDuration,
                    getRootDefaults,
                )
            ) {
                selectedIndex = rs.idx;
                saveClips(clips);
            }
            return;
        }
        const state = dragState;
        if (!state) {
            return;
        }
        endDrag(body);
        if (!state.active) {
            return;
        }
        suppressClick = true;
        const me = event as MouseEvent;
        const { rects } = readRegions(body);
        const gap = computeDropIndex(me.clientX, rects);
        const from = state.sourceIdx;
        if (isNoOpMove(from, gap)) {
            selectedIndex = from;
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
        selectedIndex = finalIndexAfterMove(from, gap);
        saveClips(moveItem(clips, from, gap));
    };

    const onDocKeyDown = (body: HTMLElement, event: Event): void => {
        if ((event as KeyboardEvent).key !== "Escape") {
            return;
        }
        if (keyframeState) {
            suppressClick = true;
            endKeyframe(body);
        }
        if (resizeState) {
            if (resizeState.active) {
                suppressClick = true;
            }
            endResize(body);
        }
        if (dragState) {
            if (dragState.active) {
                suppressClick = true;
            }
            endDrag(body);
        }
    };

    const onBodyKeyDown = (event: Event): void => {
        const ke = event as KeyboardEvent;
        if (ke.key !== "Enter" && ke.key !== " ") {
            return;
        }
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }
        const pipEl = target.closest(KEY_SELECTOR);
        if (!(pipEl instanceof HTMLElement)) {
            return;
        }
        ke.preventDefault();
        const pipRegion = pipEl.closest(REGION_SELECTOR);
        const clipIdx = parseClipIdx(pipRegion);
        const refIdx = parseRefIdx(pipEl);
        if (clipIdx === null || refIdx === null) {
            return;
        }
        applyToggleKeyframeFromEnd(clipIdx, refIdx, readStateToken());
    };

    let bodyClickHandler: ((e: Event) => void) | null = null;
    let bodyDownHandler: ((e: Event) => void) | null = null;
    let bodyKeyDownHandler: ((e: Event) => void) | null = null;
    let docMoveHandler: ((e: Event) => void) | null = null;
    let docUpHandler: ((e: Event) => void) | null = null;
    let docKeyHandler: ((e: Event) => void) | null = null;

    const attach = (body: HTMLElement): void => {
        if (attachedBody === body) {
            return;
        }
        if (attachedBody) {
            dispose();
        }
        bodyClickHandler = (e) => onRegionClick(body, e);
        bodyDownHandler = (e) => onBodyMouseDown(e);
        bodyKeyDownHandler = (e) => onBodyKeyDown(e);
        docMoveHandler = (e) => onDocMouseMove(body, e);
        docUpHandler = (e) => onDocMouseUp(body, e);
        docKeyHandler = (e) => onDocKeyDown(body, e);
        body.addEventListener("click", bodyClickHandler);
        body.addEventListener("mousedown", bodyDownHandler);
        body.addEventListener("keydown", bodyKeyDownHandler);
        document.addEventListener("mousemove", docMoveHandler);
        document.addEventListener("mouseup", docUpHandler);
        document.addEventListener("keydown", docKeyHandler);
        attachedBody = body;
    };

    const reapplySelection = (body: HTMLElement, clipCount: number): void => {
        selectedIndex = resolveSelectedIndex(selectedIndex, clipCount);
        markSelection(body);
    };

    const dispose = (): void => {
        if (attachedBody) {
            if (bodyClickHandler) {
                attachedBody.removeEventListener("click", bodyClickHandler);
            }
            if (bodyDownHandler) {
                attachedBody.removeEventListener("mousedown", bodyDownHandler);
            }
            if (bodyKeyDownHandler) {
                attachedBody.removeEventListener("keydown", bodyKeyDownHandler);
            }
            endDrag(attachedBody);
            endResize(attachedBody);
            endKeyframe(attachedBody);
        }
        if (docMoveHandler) {
            document.removeEventListener("mousemove", docMoveHandler);
        }
        if (docUpHandler) {
            document.removeEventListener("mouseup", docUpHandler);
        }
        if (docKeyHandler) {
            document.removeEventListener("keydown", docKeyHandler);
        }
        bodyClickHandler = null;
        bodyDownHandler = null;
        bodyKeyDownHandler = null;
        docMoveHandler = null;
        docUpHandler = null;
        docKeyHandler = null;
        attachedBody = null;
        dragState = null;
        resizeState = null;
        keyframeState = null;
        suppressClick = false;
    };

    const getSelectedIndex = (): number | null => selectedIndex;

    return { attach, reapplySelection, getSelectedIndex, dispose };
};
