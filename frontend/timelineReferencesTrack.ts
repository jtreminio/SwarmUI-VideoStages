import { clamp, REF_FRAME_MIN } from "./constants";
import {
    appendRefToClip,
    buildDefaultRef,
    getReferenceFrameMax,
    removeRefAt,
} from "./normalization";
import { getClips, saveClips } from "./persistence";
import { getRootDefaults } from "./rootDefaults";
import { readStateToken } from "./swarmInputs";
import { keyframeLeftPercent, keyframeTimeSeconds } from "./timelineDetail";
import { pxToFrame } from "./timelineEdit";
import { setSelection } from "./uiState";

const THUMB_SELECTOR = '.vst-refs-mark[data-vst-ref="thumb"]';
const LANE_SELECTOR = ".vst-refs-lane[data-vst-ref-add]";
const DRAGGING_CLASS = "vst-refs-dragging";
const DRAG_THRESHOLD_PX = 5;

export interface TimelineReferencesTrack {
    attach(body: HTMLElement): void;
    dispose(): void;
}

const currentFps = (): number => {
    try {
        const fps = getRootDefaults().fps;
        return typeof fps === "number" && fps > 0 ? fps : 24;
    } catch {
        return 24;
    }
};

const parseIntAttr = (el: Element | null, name: string): number | null => {
    if (!el) {
        return null;
    }
    const raw = el.getAttribute(name);
    if (raw === null) {
        return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
};

export const createTimelineReferencesTrack = (): TimelineReferencesTrack => {
    let boundBody: HTMLElement | null = null;
    let suppressClick = false;
    let refDrag: {
        clipIdx: number;
        refIdx: number;
        mark: HTMLElement;
        arrow: HTMLElement | null;
        lane: HTMLElement;
        startX: number;
        originalLeft: string;
        arrowOriginalLeft: string;
        originalLabel: string;
        durationSeconds: number;
        fps: number;
        fromEnd: boolean;
        active: boolean;
        sourceJson: string;
    } | null = null;

    const findArrow = (clipIdx: number, refIdx: number): HTMLElement | null =>
        boundBody?.querySelector<HTMLElement>(
            `.vst-region[data-clip-idx="${clipIdx}"] .vst-key[data-ref-idx="${refIdx}"]`,
        ) ?? null;

    const positionRefMarker = (
        mark: HTMLElement,
        arrow: HTMLElement | null,
        frame: number,
        fromEnd: boolean,
        durationSeconds: number,
        fps: number,
    ): void => {
        const time = keyframeTimeSeconds(frame, fromEnd, durationSeconds, fps);
        const leftPct = `${keyframeLeftPercent(time, durationSeconds)}%`;
        mark.style.left = leftPct;
        if (arrow) {
            arrow.style.left = leftPct;
        }
        const ph = mark.querySelector<HTMLElement>(".vst-refs-ph");
        if (ph) {
            ph.textContent = `R ${fromEnd ? "-" : ""}${frame}`;
        }
    };

    const isStale = (sourceJson: string): boolean =>
        readStateToken() !== sourceJson;

    const addRefAtFrame = (
        clipIdx: number,
        frame: number,
        sourceJson: string,
    ): void => {
        if (isStale(sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip) {
            return;
        }
        const frameMax = getReferenceFrameMax(getRootDefaults, clip);
        const ref = buildDefaultRef();
        ref.frame = clamp(Math.round(frame), REF_FRAME_MIN, frameMax);
        appendRefToClip(clip, ref);
        saveClips(clips);
    };

    const deleteRef = (
        clipIdx: number,
        refIdx: number,
        sourceJson: string,
    ): void => {
        if (isStale(sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip || !removeRefAt(clip, refIdx)) {
            return;
        }
        saveClips(clips);
    };

    const endRefDrag = (restore: boolean): void => {
        if (refDrag && restore) {
            refDrag.mark.style.left = refDrag.originalLeft;
            if (refDrag.arrow) {
                refDrag.arrow.style.left = refDrag.arrowOriginalLeft;
            }
            const ph = refDrag.mark.querySelector<HTMLElement>(".vst-refs-ph");
            if (ph) {
                ph.textContent = refDrag.originalLabel;
            }
        }
        refDrag = null;
        (boundBody ?? document.body).classList.remove(DRAGGING_CLASS);
    };

    const onBodyMouseDown = (event: Event): void => {
        const me = event as MouseEvent;
        if (me.button !== 0 || !(me.target instanceof Element)) {
            return;
        }
        const mark = me.target.closest(THUMB_SELECTOR);
        if (!(mark instanceof HTMLElement)) {
            return;
        }
        if (me.shiftKey) {
            me.preventDefault();
            return;
        }
        const lane = mark.closest(LANE_SELECTOR);
        const clipIdx = parseIntAttr(mark, "data-clip-idx");
        const refIdx = parseIntAttr(mark, "data-ref-idx");
        if (
            !(lane instanceof HTMLElement) ||
            clipIdx === null ||
            refIdx === null
        ) {
            return;
        }
        const clip = getClips()[clipIdx];
        const ref = clip?.refs?.[refIdx];
        if (!clip || !ref) {
            return;
        }
        const arrow = findArrow(clipIdx, refIdx);
        refDrag = {
            clipIdx,
            refIdx,
            mark,
            arrow,
            lane,
            startX: me.clientX,
            originalLeft: mark.style.left,
            arrowOriginalLeft: arrow?.style.left ?? "",
            originalLabel:
                mark.querySelector<HTMLElement>(".vst-refs-ph")?.textContent ??
                "",
            durationSeconds: clip.duration,
            fps: currentFps(),
            fromEnd: ref.fromEnd === true,
            active: false,
            sourceJson: readStateToken(),
        };
        me.preventDefault();
    };

    const dragFrameAt = (clientX: number): number => {
        if (!refDrag) {
            return REF_FRAME_MIN;
        }
        const rect = refDrag.lane.getBoundingClientRect();
        return pxToFrame(
            clientX - rect.left,
            rect.width,
            refDrag.durationSeconds,
            refDrag.fps,
            refDrag.fromEnd,
        );
    };

    const onDocMouseMove = (event: Event): void => {
        if (!refDrag) {
            return;
        }
        const me = event as MouseEvent;
        if (!refDrag.active) {
            if (Math.abs(me.clientX - refDrag.startX) < DRAG_THRESHOLD_PX) {
                return;
            }
            refDrag.active = true;
            (boundBody ?? document.body).classList.add(DRAGGING_CLASS);
        }
        positionRefMarker(
            refDrag.mark,
            refDrag.arrow,
            dragFrameAt(me.clientX),
            refDrag.fromEnd,
            refDrag.durationSeconds,
            refDrag.fps,
        );
    };

    const onDocMouseUp = (event: Event): void => {
        if (!refDrag) {
            return;
        }
        const drag = refDrag;
        const newFrame = dragFrameAt((event as MouseEvent).clientX);
        if (!drag.active) {
            endRefDrag(true);
            return;
        }
        suppressClick = true;
        const clips = getClips();
        const ref = clips[drag.clipIdx]?.refs?.[drag.refIdx];
        if (isStale(drag.sourceJson) || !ref || ref.frame === newFrame) {
            endRefDrag(true);
            return;
        }
        endRefDrag(false);
        ref.frame = newFrame;
        saveClips(clips);
    };

    const onDocKeyDown = (event: Event): void => {
        if ((event as KeyboardEvent).key !== "Escape" || !refDrag) {
            return;
        }
        if (refDrag.active) {
            suppressClick = true;
        }
        endRefDrag(true);
    };

    const selectRef = (clipIdx: number, refIdx: number): void => {
        setSelection({ kind: "ref", clipIdx, refIdx });
    };

    const onBodyClick = (event: Event): void => {
        if (suppressClick) {
            suppressClick = false;
            return;
        }
        if (!(event.target instanceof Element)) {
            return;
        }
        const thumb = event.target.closest(THUMB_SELECTOR);
        if (thumb instanceof HTMLElement) {
            const clipIdx = parseIntAttr(thumb, "data-clip-idx");
            const refIdx = parseIntAttr(thumb, "data-ref-idx");
            if (clipIdx !== null && refIdx !== null) {
                if ((event as MouseEvent).shiftKey) {
                    deleteRef(clipIdx, refIdx, readStateToken());
                } else {
                    selectRef(clipIdx, refIdx);
                }
            }
            return;
        }
        const lane = event.target.closest(LANE_SELECTOR);
        if (!(lane instanceof HTMLElement)) {
            return;
        }
        const clipIdx = parseIntAttr(lane, "data-clip-idx");
        if (clipIdx === null) {
            return;
        }
        const clip = getClips()[clipIdx];
        if (!clip) {
            return;
        }
        const rect = lane.getBoundingClientRect();
        const frame = pxToFrame(
            (event as MouseEvent).clientX - rect.left,
            rect.width,
            clip.duration,
            currentFps(),
            false,
        );
        addRefAtFrame(clipIdx, frame, readStateToken());
    };

    const onBodyKeyDown = (event: Event): void => {
        const ke = event as KeyboardEvent;
        if (ke.key !== "Enter" && ke.key !== " ") {
            return;
        }
        if (!(ke.target instanceof Element)) {
            return;
        }
        const thumb = ke.target.closest(THUMB_SELECTOR);
        if (!(thumb instanceof HTMLElement)) {
            return;
        }
        const clipIdx = parseIntAttr(thumb, "data-clip-idx");
        const refIdx = parseIntAttr(thumb, "data-ref-idx");
        if (clipIdx === null || refIdx === null) {
            return;
        }
        ke.preventDefault();
        selectRef(clipIdx, refIdx);
    };

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("click", onBodyClick);
        body.addEventListener("keydown", onBodyKeyDown);
        body.addEventListener("mousedown", onBodyMouseDown);
        document.addEventListener("mousemove", onDocMouseMove);
        document.addEventListener("mouseup", onDocMouseUp);
        document.addEventListener("keydown", onDocKeyDown);
    };

    const dispose = (): void => {
        endRefDrag(false);
        if (boundBody) {
            boundBody.removeEventListener("click", onBodyClick);
            boundBody.removeEventListener("keydown", onBodyKeyDown);
            boundBody.removeEventListener("mousedown", onBodyMouseDown);
            boundBody = null;
        }
        document.removeEventListener("mousemove", onDocMouseMove);
        document.removeEventListener("mouseup", onDocMouseUp);
        document.removeEventListener("keydown", onDocKeyDown);
        suppressClick = false;
    };

    return { attach, dispose };
};
