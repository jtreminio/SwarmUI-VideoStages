import { clamp } from "./constants";
import { getClips } from "./persistence";
import { livePxPerSecond } from "./timelineLinking";
import {
    clipIndexAtSeconds,
    computeRegionLayout,
    formatPlayheadReadout,
    type RegionLayout,
    resolvePlayheadSeconds,
    TRACK_HEADER_W_PX,
    zoomAnchorTime,
} from "./timelineView";

const SCRUB_SELECTOR =
    ".vst-ruler, [data-vst-playhead-handle], [data-vst-playhead-hit]";

/**
 * View-only playhead scrubbing. Owns the vertical cursor line + ruler handle and
 * a live toolbar readout. Positions are snapped to the frame grid and clamped to
 * [0, total]. Dragging updates the DOM directly; on release it commits the new
 * position through `setSeconds` (which persists it in the timeline VIEW state and
 * re-renders). It NEVER touches clip data — no saveClips / param writes.
 */
export interface TimelinePlayheadDeps {
    /** Persist the committed playhead position (view-state) and refresh. */
    setSeconds: (seconds: number) => void;
}

export interface TimelinePlayhead {
    attach(body: HTMLElement): void;
    dispose(): void;
}

interface ScrubState {
    layouts: RegionLayout[];
    totalSeconds: number;
    pps: number;
    fps: number;
}

const liveFps = (body: HTMLElement): number => {
    const fps = Number.parseFloat(body.dataset.vstFps ?? "");
    return Number.isFinite(fps) && fps > 0 ? fps : 24;
};

export const createTimelinePlayhead = (
    deps: TimelinePlayheadDeps,
): TimelinePlayhead => {
    let boundBody: HTMLElement | null = null;
    let scrub: ScrubState | null = null;

    const secondsFromClientX = (body: HTMLElement, clientX: number): number => {
        if (!scrub) {
            return 0;
        }
        const scroll = body.querySelector<HTMLElement>(".vst-scroll");
        const rect = scroll?.getBoundingClientRect();
        const offsetX = clientX - (rect?.left ?? 0);
        const raw = zoomAnchorTime(
            offsetX,
            scroll?.scrollLeft ?? 0,
            scrub.pps,
            TRACK_HEADER_W_PX,
        );
        return resolvePlayheadSeconds(
            clamp(raw, 0, scrub.totalSeconds),
            scrub.totalSeconds,
            scrub.fps,
        );
    };

    const paint = (body: HTMLElement, seconds: number): void => {
        if (!scrub) {
            return;
        }
        const rulerPx = seconds * scrub.pps;
        const planePx = TRACK_HEADER_W_PX + rulerPx;
        const line = body.querySelector<HTMLElement>("[data-vst-playhead]");
        if (line) {
            line.style.left = `${planePx}px`;
        }
        const handle = body.querySelector<HTMLElement>(
            "[data-vst-playhead-handle]",
        );
        if (handle) {
            handle.style.left = `${rulerPx}px`;
        }
        const readout = body.querySelector<HTMLElement>(
            "[data-vst-readout-head]",
        );
        if (readout) {
            readout.textContent = formatPlayheadReadout(seconds, scrub.fps);
        }
        const headClipIdx = clipIndexAtSeconds(scrub.layouts, seconds);
        for (const el of body.querySelectorAll<HTMLElement>(
            ".vst-region.vst-under-head",
        )) {
            el.classList.remove("vst-under-head");
        }
        if (headClipIdx !== null) {
            body.querySelector<HTMLElement>(
                `.vst-region[data-clip-idx="${headClipIdx}"]`,
            )?.classList.add("vst-under-head");
        }
    };

    const onBodyMouseDown = (body: HTMLElement, event: Event): void => {
        const me = event as MouseEvent;
        if (me.button !== 0 || !(me.target instanceof Element)) {
            return;
        }
        if (!me.target.closest(SCRUB_SELECTOR)) {
            return;
        }
        const pps = livePxPerSecond(body);
        const layouts = computeRegionLayout(getClips(), { pxPerSecond: pps });
        const totalSeconds = layouts.reduce(
            (sum, l) => sum + l.durationSeconds,
            0,
        );
        scrub = { layouts, totalSeconds, pps, fps: liveFps(body) };
        // Own this gesture: a hit-area/ruler press must not start a region drag.
        me.stopImmediatePropagation();
        me.preventDefault();
        paint(body, secondsFromClientX(body, me.clientX));
    };

    const onDocMouseMove = (body: HTMLElement, event: Event): void => {
        if (!scrub) {
            return;
        }
        paint(body, secondsFromClientX(body, (event as MouseEvent).clientX));
    };

    const onDocMouseUp = (body: HTMLElement, event: Event): void => {
        if (!scrub) {
            return;
        }
        const seconds = secondsFromClientX(body, (event as MouseEvent).clientX);
        scrub = null;
        deps.setSeconds(seconds);
    };

    let downHandler: ((event: Event) => void) | null = null;
    let moveHandler: ((event: Event) => void) | null = null;
    let upHandler: ((event: Event) => void) | null = null;

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        downHandler = (event) => onBodyMouseDown(body, event);
        moveHandler = (event) => onDocMouseMove(body, event);
        upHandler = (event) => onDocMouseUp(body, event);
        body.addEventListener("mousedown", downHandler);
        document.addEventListener("mousemove", moveHandler);
        document.addEventListener("mouseup", upHandler);
    };

    const dispose = (): void => {
        if (boundBody && downHandler) {
            boundBody.removeEventListener("mousedown", downHandler);
        }
        if (moveHandler) {
            document.removeEventListener("mousemove", moveHandler);
        }
        if (upHandler) {
            document.removeEventListener("mouseup", upHandler);
        }
        downHandler = null;
        moveHandler = null;
        upHandler = null;
        scrub = null;
        boundBody = null;
    };

    return { attach, dispose };
};
