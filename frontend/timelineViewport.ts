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
    stripCollapsed(): boolean;
    setStripCollapsed(collapsed: boolean): void;
    toggleUnit(): void;
    zoomIn(): void;
    zoomOut(): void;
    zoomFit(): void;
    setZoom(value: number): void;
    zoomWheel(factor: number, clientX: number): void;
    restoreScroll(prevScrollLeft: number): void;
}

export const createTimelineViewport = (options: {
    refresh: () => void;
    totalSeconds: () => number;
    timelineBody: () => HTMLElement | null;
    scrollElement: () => HTMLElement | null;
}): TimelineViewport => {
    let currentUnit: TimelineUnit = "seconds";
    let currentPxPerSecond = DEFAULT_PX_PER_SECOND;
    let collapsed = false;
    let lastRenderedPxPerSecond = 0;

    const save = (): void => {
        saveViewState({
            pxPerSecond: currentPxPerSecond,
            unit: currentUnit,
            stripCollapsed: collapsed,
        });
    };

    const load = (): void => {
        const stored = loadViewState();
        if (!stored) return;
        if (stored.pxPerSecond !== undefined) {
            currentPxPerSecond = clampPxPerSecond(stored.pxPerSecond);
        }
        if (stored.unit) currentUnit = stored.unit;
        if (stored.stripCollapsed !== undefined) {
            collapsed = stored.stripCollapsed;
        }
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

    const restoreScroll = (prevScrollLeft: number): void => {
        if (prevScrollLeft > 0) {
            const target =
                lastRenderedPxPerSecond > 0 &&
                lastRenderedPxPerSecond !== currentPxPerSecond
                    ? zoomAnchorScrollLeft(
                          zoomAnchorTime(
                              TRACK_HEADER_W_PX,
                              prevScrollLeft,
                              lastRenderedPxPerSecond,
                          ),
                          currentPxPerSecond,
                          TRACK_HEADER_W_PX,
                      )
                    : prevScrollLeft;
            const fresh = options.scrollElement();
            if (fresh) fresh.scrollLeft = target;
        }
        lastRenderedPxPerSecond = currentPxPerSecond;
    };

    return {
        load,
        unit: () => currentUnit,
        pxPerSecond: () => currentPxPerSecond,
        stripCollapsed: () => collapsed,
        setStripCollapsed: (value) => {
            collapsed = value;
            save();
        },
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
