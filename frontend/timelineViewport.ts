import type { TimelineUnit } from "./timelineDetail";
import {
    clampPxPerSecond,
    computeFitPxPerSecond,
    DEFAULT_PX_PER_SECOND,
    TRACK_HEADER_W_PX,
    ZOOM_FACTOR,
    zoomAnchorScrollLeft,
    zoomAnchorTime,
} from "./timelineView";
import { loadViewState, saveViewState } from "./timelineViewState";

export interface TimelineViewport {
    load(): void;
    unit(): TimelineUnit;
    pxPerSecond(): number;
    toggleUnit(): void;
    zoomIn(): void;
    zoomOut(): void;
    zoomFit(): void;
    setZoom(value: number): void;
    zoomWheel(factor: number, clientX: number): void;
    restoreScroll(previous: TimelineScrollPosition): void;
}

export interface TimelineScrollPosition {
    left: number;
    top: number;
}

export const createTimelineViewport = (options: {
    refresh: () => void;
    totalSeconds: () => number;
    timelineBody: () => HTMLElement | null;
    scrollElement: () => HTMLElement | null;
}): TimelineViewport => {
    let currentUnit: TimelineUnit = "seconds";
    let currentPxPerSecond = DEFAULT_PX_PER_SECOND;
    let lastRenderedPxPerSecond = 0;

    const save = (): void => {
        saveViewState({
            pxPerSecond: currentPxPerSecond,
            unit: currentUnit,
        });
    };

    const load = (): void => {
        const stored = loadViewState();
        if (!stored) return;
        if (stored.pxPerSecond !== undefined) {
            currentPxPerSecond = clampPxPerSecond(stored.pxPerSecond);
        }
        if (stored.unit) currentUnit = stored.unit;
    };

    const setZoom = (value: number): void => {
        currentPxPerSecond = clampPxPerSecond(value);
        save();
        options.refresh();
    };

    const zoomWheel = (factor: number, clientX: number): void => {
        const scroll = options.scrollElement();
        if (!scroll || currentPxPerSecond <= 0) {
            setZoom(currentPxPerSecond * factor);
            return;
        }
        const offsetX = clientX - scroll.getBoundingClientRect().left;
        const timeAtPointer = zoomAnchorTime(
            offsetX,
            scroll.scrollLeft,
            currentPxPerSecond,
        );
        setZoom(currentPxPerSecond * factor);
        const fresh = options.scrollElement();
        if (fresh) {
            fresh.scrollLeft = zoomAnchorScrollLeft(
                timeAtPointer,
                currentPxPerSecond,
                offsetX,
            );
        }
    };

    const restoreScroll = (previous: TimelineScrollPosition): void => {
        const fresh = options.scrollElement();
        if (fresh && previous.left > 0) {
            const target =
                lastRenderedPxPerSecond > 0 &&
                lastRenderedPxPerSecond !== currentPxPerSecond
                    ? zoomAnchorScrollLeft(
                          zoomAnchorTime(
                              TRACK_HEADER_W_PX,
                              previous.left,
                              lastRenderedPxPerSecond,
                          ),
                          currentPxPerSecond,
                          TRACK_HEADER_W_PX,
                      )
                    : previous.left;
            fresh.scrollLeft = target;
        }
        if (fresh && previous.top > 0) {
            fresh.scrollTop = previous.top;
        }
        lastRenderedPxPerSecond = currentPxPerSecond;
    };

    return {
        load,
        unit: () => currentUnit,
        pxPerSecond: () => currentPxPerSecond,
        toggleUnit: () => {
            currentUnit = currentUnit === "seconds" ? "frames" : "seconds";
            save();
            options.refresh();
        },
        zoomIn: () => setZoom(currentPxPerSecond * ZOOM_FACTOR),
        zoomOut: () => setZoom(currentPxPerSecond / ZOOM_FACTOR),
        zoomFit: () => {
            const width =
                options.scrollElement()?.clientWidth ??
                options.timelineBody()?.clientWidth ??
                0;
            setZoom(
                computeFitPxPerSecond(
                    options.totalSeconds(),
                    width,
                    TRACK_HEADER_W_PX + 24,
                ),
            );
        },
        setZoom,
        zoomWheel,
        restoreScroll,
    };
};
