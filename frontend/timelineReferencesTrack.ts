import { referenceEndpointPolicy } from "./architectures/referenceEndpoints";
import { resolvedClipFrameGrid } from "./architectures/temporalGrid";
import type { AuthoringTransactionSnapshot } from "./authoringSnapshot";
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
import { getClips, getState, getTimelineStore } from "./persistence/repository";
import { nextAllowedReferencePosition } from "./referenceAuthoring";
import { activateSelection, setSelection } from "./selection";
import { getTimelineAuthoringSettings } from "./timelineAuthoringSettings";
import { keyframeTimeSeconds } from "./timelineDetail";
import { pxToFrame } from "./timelineEdit";
import { SNAP_THRESHOLD_PX, snapPoint } from "./timelineSnap";
import {
    commitClipMutation,
    currentRevision,
    isActivateKey,
    keyframeLeftPercent,
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
    generatedDurationSeconds: number;
    fps: number;
    policy: ReferenceDragPolicy;
    fromEnd: boolean;
    sourceRevision: number;
}

interface ReferenceDragPolicy {
    supported: boolean;
    positions: string[];
    frameGrid: number;
    frameMax: number;
}

export const createTimelineReferencesTrack = (
    getAuthoring: () => AuthoringTransactionSnapshot,
): TimelineReferencesTrack => {
    let boundBody: HTMLElement | null = null;
    let unregister: (() => void) | null = null;
    const canEditReferences = (
        clip: ReturnType<typeof getClips>[number],
        authoring = getAuthoring(),
    ) =>
        authoring.capabilities.forClip(clip).decision("frameReferences")
            .supported;
    const referencePositions = (
        clip: ReturnType<typeof getClips>[number],
        authoring = getAuthoring(),
    ): string[] =>
        referenceEndpointPolicy(clip, authoring.defaults.modelCatalog)
            .positions;
    const resolveDragPolicy = (
        clip: ReturnType<typeof getClips>[number],
        fps: number,
        authoring: AuthoringTransactionSnapshot,
    ): ReferenceDragPolicy => ({
        supported: canEditReferences(clip, authoring),
        positions: referencePositions(clip, authoring),
        frameGrid: resolvedClipFrameGrid(clip, authoring.defaults.modelCatalog),
        frameMax: getReferenceFrameMax(() => authoring.defaults, clip, fps),
    });
    const sameDragPolicy = (
        left: ReferenceDragPolicy,
        right: ReferenceDragPolicy,
    ): boolean =>
        left.supported === right.supported &&
        left.frameGrid === right.frameGrid &&
        left.frameMax === right.frameMax &&
        left.positions.length === right.positions.length &&
        left.positions.every(
            (position, index) => position === right.positions[index],
        );

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
        sourceRevision: number,
        authoring = getAuthoring(),
    ): void => {
        const fps = documentFps(getState());
        let newRefIdx = -1;
        const saved = commitClipMutation(
            sourceRevision,
            "references-track",
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip || !canEditReferences(clip, authoring)) {
                    return null;
                }
                const frameMax = getReferenceFrameMax(
                    () => authoring.defaults,
                    clip,
                    fps,
                );
                const allowed = referencePositions(clip, authoring);
                const ref = buildDefaultRef();
                if (allowed.includes("any")) {
                    ref.frame = clamp(
                        Math.round(frame),
                        REF_FRAME_MIN,
                        frameMax,
                    );
                } else {
                    const position = nextAllowedReferencePosition(
                        clip.refs,
                        frameMax,
                        allowed,
                    );
                    if (!position) {
                        return null;
                    }
                    ref.frame = position.frame;
                    ref.fromEnd = position.fromEnd;
                }
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
        sourceRevision: number,
    ): void => {
        commitClipMutation(sourceRevision, "references-track", (clips) => {
            const clip = clips[clipIdx];
            return clip && removeRefAt(clip, refIdx) ? clips : null;
        });
    };

    const dragPositionAt = (
        state: RefDragState,
        clientX: number,
    ): { frame: number; fromEnd: boolean } => {
        const bounded =
            state.policy.positions.length > 0 &&
            !state.policy.positions.includes("any");
        if (bounded) {
            const rect = state.lane.getBoundingClientRect();
            const prefersLast = clientX - rect.left >= rect.width / 2;
            const supportsFirst = state.policy.positions.includes("first");
            const supportsLast = state.policy.positions.includes("last");
            return {
                frame: REF_FRAME_MIN,
                fromEnd: supportsLast && (!supportsFirst || prefersLast),
            };
        }
        const rect = state.lane.getBoundingClientRect();
        const frame = pxToFrame(
            clientX - rect.left,
            rect.width,
            state.durationSeconds,
            state.fps,
            state.fromEnd,
            state.policy.frameGrid,
        );
        if (!getTimelineAuthoringSettings().snap || rect.width <= 0) {
            return { frame, fromEnd: state.fromEnd };
        }
        const thresholdFrames = Math.max(
            1,
            (SNAP_THRESHOLD_PX / rect.width) * state.policy.frameMax,
        );
        return {
            frame: Math.round(
                snapPoint(
                    frame,
                    [],
                    [REF_FRAME_MIN, state.policy.frameMax],
                    thresholdFrames,
                ),
            ),
            fromEnd: state.fromEnd,
        };
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
            const position = dragPositionAt(state, ctx.event.clientX);
            positionRefMarker(
                state.mark,
                state.arrow,
                position.frame,
                position.fromEnd,
                state.generatedDurationSeconds,
                state.fps,
            );
        },
        onCommit: (ctx) => {
            body.classList.remove(DRAGGING_CLASS);
            if (!state.mark.isConnected || !state.lane.isConnected) {
                restoreDragPreview(state);
                return;
            }
            const position = dragPositionAt(state, ctx.event.clientX);
            const saved = commitClipMutation(
                state.sourceRevision,
                "references-track",
                (clips) => {
                    const clip = clips[state.clipIdx];
                    const ref = clip?.refs?.[state.refIdx];
                    const livePolicy = clip
                        ? resolveDragPolicy(clip, state.fps, getAuthoring())
                        : null;
                    if (!ref || !livePolicy) {
                        return null;
                    }
                    if (
                        !livePolicy.supported ||
                        !sameDragPolicy(state.policy, livePolicy) ||
                        (ref.frame === position.frame &&
                            ref.fromEnd === position.fromEnd)
                    ) {
                        return null;
                    }
                    ref.frame = position.frame;
                    ref.fromEnd = position.fromEnd;
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
        const documentSnapshot = getTimelineStore().getSnapshot();
        const clip = documentSnapshot.state.clips[clipIdx];
        const ref = clip?.refs?.[refIdx];
        if (!clip || !ref) {
            return null;
        }
        const fps = documentFps(documentSnapshot.state);
        const policy = resolveDragPolicy(clip, fps, getAuthoring());
        if (!policy.supported) {
            me.preventDefault();
            return claimOnly();
        }
        const arrow = findArrow(clipIdx, refIdx);
        if (policy.positions.length === 0) {
            me.preventDefault();
            return claimOnly();
        }
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
            generatedDurationSeconds: policy.frameMax / fps,
            fps,
            policy,
            fromEnd: ref.fromEnd === true,
            sourceRevision: documentSnapshot.revision,
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
                    deleteRef(clipIdx, refIdx, currentRevision());
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
        const authoring = getAuthoring();
        if (!canEditReferences(clip, authoring)) {
            return;
        }
        const rect = lane.getBoundingClientRect();
        const frame = pxToFrame(
            (event as MouseEvent).clientX - rect.left,
            rect.width,
            clip.duration,
            documentFps(getState()),
            false,
            resolvedClipFrameGrid(clip, authoring.defaults.modelCatalog),
        );
        addRefAtFrame(clipIdx, frame, currentRevision(), authoring);
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
