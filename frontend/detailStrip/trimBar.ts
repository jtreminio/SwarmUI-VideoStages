import {
    type SourceRange,
    setInPoint,
    setOutPoint,
    slideRange,
    type TrimLimits,
    toInOut,
} from "../trimGeometry";

export interface TrimBarSpec {
    range: SourceRange;
    limits: TrimLimits;
    onChange(next: SourceRange): void;
}

type TrimGrip = "in" | "out" | "window";

const NUDGE_SECONDS = 0.1;
const COARSE_NUDGE_SECONDS = 1;

export const pointerSecondsAt = (
    clientX: number,
    trackLeft: number,
    trackWidth: number,
    limitSeconds: number,
): number => {
    if (!(trackWidth > 0) || !(limitSeconds > 0)) {
        return 0;
    }
    const ratio = (clientX - trackLeft) / trackWidth;
    return Math.min(limitSeconds, Math.max(0, ratio * limitSeconds));
};

export const trimBarGeometry = (
    range: SourceRange,
    limitSeconds: number,
): { leftPct: number; widthPct: number } => {
    if (!(limitSeconds > 0)) {
        return { leftPct: 0, widthPct: 100 };
    }
    const { inSeconds, outSeconds } = toInOut(range);
    const leftPct = Math.min(
        100,
        Math.max(0, (inSeconds / limitSeconds) * 100),
    );
    const rightPct = Math.min(
        100,
        Math.max(0, (outSeconds / limitSeconds) * 100),
    );
    return { leftPct, widthPct: Math.max(0, rightPct - leftPct) };
};

const applyGrip = (
    grip: TrimGrip,
    range: SourceRange,
    seconds: number,
    limits: TrimLimits,
): SourceRange => {
    if (grip === "in") {
        return setInPoint(range, seconds, limits);
    }
    if (grip === "out") {
        return setOutPoint(range, seconds, limits);
    }
    return slideRange(range, seconds, limits);
};

export interface TrimBarHandle {
    element: HTMLElement;
    sync(range: SourceRange): void;
}

export const buildTrimBar = (spec: TrimBarSpec): TrimBarHandle => {
    let range = spec.range;
    const { limits } = spec;

    const wrap = document.createElement("div");
    wrap.className = "vst-trim";

    const track = document.createElement("div");
    track.className = "vst-trim-track";
    wrap.appendChild(track);

    const window_ = document.createElement("div");
    window_.className = "vst-trim-window";
    window_.setAttribute("role", "slider");
    window_.setAttribute("aria-label", "Trimmed range position");
    window_.tabIndex = 0;
    track.appendChild(window_);

    const grips: Record<"in" | "out", HTMLElement> = {
        in: document.createElement("span"),
        out: document.createElement("span"),
    };
    for (const edge of ["in", "out"] as const) {
        const grip = grips[edge];
        grip.className = `vst-trim-grip vst-trim-grip-${edge}`;
        grip.dataset.vstTrimGrip = edge;
        grip.setAttribute("role", "slider");
        grip.setAttribute(
            "aria-label",
            edge === "in" ? "In point" : "Out point",
        );
        grip.tabIndex = 0;
        track.appendChild(grip);
    }

    const scale = document.createElement("div");
    scale.className = "vst-trim-scale";
    const scaleStart = document.createElement("span");
    scaleStart.textContent = "0.0";
    const scaleEnd = document.createElement("span");
    scaleEnd.textContent = `${limits.limitSeconds.toFixed(1)}`;
    scale.append(scaleStart, scaleEnd);
    wrap.appendChild(scale);

    const paint = (): void => {
        const { leftPct, widthPct } = trimBarGeometry(
            range,
            limits.limitSeconds,
        );
        window_.style.left = `${leftPct}%`;
        window_.style.width = `${widthPct}%`;
        grips.in.style.left = `${leftPct}%`;
        grips.out.style.left = `${leftPct + widthPct}%`;

        const { inSeconds, outSeconds } = toInOut(range);
        const describe = (
            element: HTMLElement,
            now: number,
            max: number,
            text: string,
        ): void => {
            element.setAttribute("aria-valuemin", "0");
            element.setAttribute("aria-valuemax", `${max}`);
            element.setAttribute("aria-valuenow", `${now}`);
            element.setAttribute("aria-valuetext", text);
        };
        describe(
            grips.in,
            inSeconds,
            limits.limitSeconds,
            `In point, ${inSeconds.toFixed(1)} seconds`,
        );
        describe(
            grips.out,
            outSeconds,
            limits.limitSeconds,
            `Out point, ${outSeconds.toFixed(1)} seconds`,
        );
        describe(
            window_,
            inSeconds,
            limits.limitSeconds,
            `Trimmed range, ${inSeconds.toFixed(1)} to ${outSeconds.toFixed(1)} seconds`,
        );
        grips.in.title = `In point — ${inSeconds.toFixed(1)} s`;
        grips.out.title = `Out point — ${outSeconds.toFixed(1)} s`;
        window_.title = "Drag to slide the range without changing its length";
    };

    const push = (next: SourceRange): void => {
        if (
            next.startSeconds === range.startSeconds &&
            next.lengthSeconds === range.lengthSeconds
        ) {
            return;
        }
        range = next;
        paint();
        spec.onChange(next);
    };

    const startDrag = (grip: TrimGrip, event: PointerEvent): void => {
        if (!(limits.limitSeconds > 0) || event.button !== 0) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        const target = event.currentTarget as HTMLElement;
        target.setPointerCapture?.(event.pointerId);
        const rect = track.getBoundingClientRect();
        const pressed = toInOut(range);
        // Preserve the cursor's offset within the window during a drag.
        const pressSeconds = pointerSecondsAt(
            event.clientX,
            rect.left,
            rect.width,
            limits.limitSeconds,
        );
        const grabOffset = pressSeconds - pressed.inSeconds;
        const before = range;

        const secondsFor = (moveEvent: PointerEvent): number => {
            const at = pointerSecondsAt(
                moveEvent.clientX,
                rect.left,
                rect.width,
                limits.limitSeconds,
            );
            return grip === "window" ? at - grabOffset : at;
        };
        const onMove = (moveEvent: PointerEvent): void => {
            push(applyGrip(grip, range, secondsFor(moveEvent), limits));
        };
        const finish = (commit: boolean, moveEvent?: PointerEvent): void => {
            target.removeEventListener("pointermove", onMove);
            target.removeEventListener("pointerup", onUp);
            target.removeEventListener("pointercancel", onCancel);
            document.removeEventListener("keydown", onKey, true);
            target.releasePointerCapture?.(event.pointerId);
            push(
                commit && moveEvent
                    ? applyGrip(grip, range, secondsFor(moveEvent), limits)
                    : before,
            );
        };
        const onUp = (upEvent: PointerEvent): void => finish(true, upEvent);
        const onCancel = (): void => finish(false);
        const onKey = (keyEvent: KeyboardEvent): void => {
            if (keyEvent.key === "Escape") {
                keyEvent.preventDefault();
                finish(false);
            }
        };
        target.addEventListener("pointermove", onMove);
        target.addEventListener("pointerup", onUp);
        target.addEventListener("pointercancel", onCancel);
        document.addEventListener("keydown", onKey, true);
    };

    const onKeyGrip = (grip: TrimGrip, event: KeyboardEvent): void => {
        const step = event.shiftKey ? COARSE_NUDGE_SECONDS : NUDGE_SECONDS;
        const { inSeconds, outSeconds } = toInOut(range);
        const at = grip === "out" ? outSeconds : inSeconds;
        let next: number | null = null;
        if (event.key === "ArrowLeft") {
            next = at - step;
        } else if (event.key === "ArrowRight") {
            next = at + step;
        } else if (event.key === "Home") {
            next = 0;
        } else if (event.key === "End") {
            next = limits.limitSeconds;
        }
        if (next === null) {
            return;
        }
        event.preventDefault();
        push(applyGrip(grip, range, next, limits));
    };

    for (const edge of ["in", "out"] as const) {
        grips[edge].addEventListener("pointerdown", (event) =>
            startDrag(edge, event),
        );
        grips[edge].addEventListener("keydown", (event) =>
            onKeyGrip(edge, event),
        );
    }
    window_.addEventListener("pointerdown", (event) =>
        startDrag("window", event),
    );
    window_.addEventListener("keydown", (event) => onKeyGrip("window", event));

    // Let the track pull the nearer edge so the narrow grip is optional.
    track.addEventListener("pointerdown", (event) => {
        if (event.target !== track || !(limits.limitSeconds > 0)) {
            return;
        }
        const rect = track.getBoundingClientRect();
        const at = pointerSecondsAt(
            event.clientX,
            rect.left,
            rect.width,
            limits.limitSeconds,
        );
        const { inSeconds, outSeconds } = toInOut(range);
        push(
            applyGrip(
                Math.abs(at - inSeconds) <= Math.abs(at - outSeconds)
                    ? "in"
                    : "out",
                range,
                at,
                limits,
            ),
        );
    });

    paint();
    return {
        element: wrap,
        sync: (next) => {
            range = next;
            paint();
        },
    };
};
