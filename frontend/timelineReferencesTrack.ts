import type { CapabilityViewResolver } from "./architectures/policy";
import { clamp, REF_FRAME_MIN } from "./constants";
import { documentFps } from "./documentQueries";
import {
    claimOnly,
    type GestureRouter,
    type GestureSession,
} from "./gestureRouter";
import {
    appendRefToClip,
    buildDefaultRef,
    getReferenceFrameMax,
    removeRefAt,
} from "./normalization";
import { getClips, getState } from "./persistence";
import { getRootDefaults } from "./rootDefaults";
import { activateSelection, setSelection } from "./selection";
import { readStateToken } from "./swarmInputs";
import { keyframeLeftPercent, keyframeTimeSeconds } from "./timelineDetail";
import { pxToFrame } from "./timelineEdit";
import {
    commitClipMutation,
    isActivateKey,
    parseIntAttr,
} from "./trackDomUtils";

const THUMB_SELECTOR = '.vst-refs-mark[data-vst-ref="thumb"]';
const LANE_SELECTOR = ".vst-refs-lane[data-vst-ref-add]";
const DRAGGING_CLASS = "vst-refs-dragging";
const DRAG_THRESHOLD_PX = 5;

export interface TimelineReferencesTrack {
    attach(body: HTMLElement, router: GestureRouter): void;
    dispose(): void;
}

interface RefDragState {
    clipIdx: number;
    refIdx: number;
    mark: HTMLElement;
    arrow: HTMLElement | null;
    lane: HTMLElement;
    originalLeft: string;
    arrowOriginalLeft: string;
    originalLabel: string;
    durationSeconds: number;
    fps: number;
    fromEnd: boolean;
    sourceJson: string;
}

export const createTimelineReferencesTrack = (
    getCapabilities?: () => CapabilityViewResolver,
): TimelineReferencesTrack => {
    let boundBody: HTMLElement | null = null;
    let unregister: (() => void) | null = null;
    const canEditReferences = (clip: ReturnType<typeof getClips>[number]) =>
        getCapabilities?.().forClip(clip).decision("frameReferences")
            .supported ?? true;

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

    const addRefAtFrame = (
        clipIdx: number,
        frame: number,
        sourceJson: string,
    ): void => {
        const fps = documentFps(getState());
        let newRefIdx = -1;
        const saved = commitClipMutation(
            sourceJson,
            "references-track",
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip || !canEditReferences(clip)) {
                    return null;
                }
                const frameMax = getReferenceFrameMax(
                    getRootDefaults,
                    clip,
                    fps,
                );
                const ref = buildDefaultRef();
                ref.frame = clamp(Math.round(frame), REF_FRAME_MIN, frameMax);
                appendRefToClip(clip, ref);
                newRefIdx = clip.refs.length - 1;
                return clips;
            },
        );
        if (saved) {
            // Open the new ref in the dock — after the save, so the rebuilt
            // ref panel already contains its row (works even when another ref
            // was selected: the same-panel path is a targeted highlight swap).
            setSelection({ kind: "ref", clipIdx, refIdx: newRefIdx });
        }
    };

    const deleteRef = (
        clipIdx: number,
        refIdx: number,
        sourceJson: string,
    ): void => {
        commitClipMutation(sourceJson, "references-track", (clips) => {
            const clip = clips[clipIdx];
            return clip && removeRefAt(clip, refIdx) ? clips : null;
        });
    };

    const dragFrameAt = (state: RefDragState, clientX: number): number => {
        const rect = state.lane.getBoundingClientRect();
        return pxToFrame(
            clientX - rect.left,
            rect.width,
            state.durationSeconds,
            state.fps,
            state.fromEnd,
        );
    };

    const restoreDragPreview = (state: RefDragState): void => {
        state.mark.style.left = state.originalLeft;
        if (state.arrow) {
            state.arrow.style.left = state.arrowOriginalLeft;
        }
        const ph = state.mark.querySelector<HTMLElement>(".vst-refs-ph");
        if (ph) {
            ph.textContent = state.originalLabel;
        }
    };

    const dragSession = (
        body: HTMLElement,
        state: RefDragState,
    ): GestureSession => ({
        threshold: DRAG_THRESHOLD_PX,
        suppressEscapeClick: true,
        onMove: (ctx) => {
            body.classList.add(DRAGGING_CLASS);
            positionRefMarker(
                state.mark,
                state.arrow,
                dragFrameAt(state, ctx.event.clientX),
                state.fromEnd,
                state.durationSeconds,
                state.fps,
            );
        },
        onCommit: (ctx) => {
            body.classList.remove(DRAGGING_CLASS);
            const newFrame = dragFrameAt(state, ctx.event.clientX);
            const saved = commitClipMutation(
                state.sourceJson,
                "references-track",
                (clips) => {
                    const ref = clips[state.clipIdx]?.refs?.[state.refIdx];
                    if (!ref || ref.frame === newFrame) {
                        return null;
                    }
                    ref.frame = newFrame;
                    return clips;
                },
            );
            if (!saved) {
                restoreDragPreview(state);
            }
        },
        onTap: () => restoreDragPreview(state),
        onCancel: () => {
            restoreDragPreview(state);
            body.classList.remove(DRAGGING_CLASS);
        },
    });

    const onPress = (
        me: MouseEvent,
        body: HTMLElement,
    ): GestureSession | null => {
        if (!(me.target instanceof Element)) {
            return null;
        }
        const mark = me.target.closest(THUMB_SELECTOR);
        if (!(mark instanceof HTMLElement)) {
            return null;
        }
        if (me.shiftKey) {
            // The thumb owns this press; the shift-CLICK delete stays in
            // onBodyClick.
            me.preventDefault();
            return claimOnly();
        }
        const lane = mark.closest(LANE_SELECTOR);
        const clipIdx = parseIntAttr(mark, "data-clip-idx");
        const refIdx = parseIntAttr(mark, "data-ref-idx");
        if (
            !(lane instanceof HTMLElement) ||
            clipIdx === null ||
            refIdx === null
        ) {
            return null;
        }
        const clip = getClips()[clipIdx];
        const ref = clip?.refs?.[refIdx];
        if (!clip || !ref) {
            return null;
        }
        if (!canEditReferences(clip)) {
            me.preventDefault();
            return claimOnly();
        }
        const arrow = findArrow(clipIdx, refIdx);
        me.preventDefault();
        return dragSession(body, {
            clipIdx,
            refIdx,
            mark,
            arrow,
            lane,
            originalLeft: mark.style.left,
            arrowOriginalLeft: arrow?.style.left ?? "",
            originalLabel:
                mark.querySelector<HTMLElement>(".vst-refs-ph")?.textContent ??
                "",
            durationSeconds: clip.duration,
            fps: documentFps(getState()),
            fromEnd: ref.fromEnd === true,
            sourceJson: readStateToken(),
        });
    };

    const selectRef = (clipIdx: number, refIdx: number): void => {
        activateSelection({ kind: "ref", clipIdx, refIdx });
    };

    const onBodyClick = (event: Event): void => {
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
        if (!canEditReferences(clip)) {
            return;
        }
        const rect = lane.getBoundingClientRect();
        const frame = pxToFrame(
            (event as MouseEvent).clientX - rect.left,
            rect.width,
            clip.duration,
            documentFps(getState()),
            false,
        );
        addRefAtFrame(clipIdx, frame, readStateToken());
    };

    const onBodyKeyDown = (event: Event): void => {
        const ke = event as KeyboardEvent;
        if (!isActivateKey(ke)) {
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

    const attach = (body: HTMLElement, router: GestureRouter): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("click", onBodyClick);
        body.addEventListener("keydown", onBodyKeyDown);
        unregister = router.register({
            id: "references",
            priority: 30,
            onPress: (me) => onPress(me, body),
        });
    };

    const dispose = (): void => {
        if (boundBody) {
            boundBody.removeEventListener("click", onBodyClick);
            boundBody.removeEventListener("keydown", onBodyKeyDown);
            boundBody = null;
        }
        unregister?.();
        unregister = null;
    };

    return { attach, dispose };
};
