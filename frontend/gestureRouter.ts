/**
 * Press-drag gesture arbitration for the shared tracks body.
 *
 * Replaces the old implicit protocol — five modules' bubble-phase mousedown
 * handlers whose priority was attach-call order plus stopImmediatePropagation,
 * each with its own doc-level move/up/Escape listeners and suppress-click flag
 * — with one explicit priority table. Tracks register routes; on a press the
 * highest-priority route to return a session owns the gesture. The router owns
 * the shared lifecycle: activation threshold, document move/up, Escape cancel,
 * and the one-shot post-drag click swallow.
 *
 * Click-only handlers (select, shift-delete, chips) stay module-owned; the
 * router only touches the click that concludes one of ITS gestures.
 */

export interface GestureMoveCtx {
    event: MouseEvent;
    /** clientX - pressX */
    dx: number;
    /** clientY - pressY */
    dy: number;
}

export interface GestureSession {
    /** Live preview; called only once the session is active. */
    onMove(ctx: GestureMoveCtx): void;
    /** Mouseup after activation. Commit (with the track's own stale guards). */
    onCommit(ctx: GestureMoveCtx): void;
    /** Mouseup before activation (a press that never became a drag). */
    onTap?(ctx: GestureMoveCtx): void;
    /** Escape or router teardown. Restore preview visuals; never commit. */
    onCancel?(): void;
    /** Activation distance in px. Default 5. */
    threshold?: number;
    /** Distance measure for activation: |dx| (default) or hypot(dx, dy). */
    axis?: "x" | "xy";
    /**
     * Swallow the concluding click even for a tap (window tracks' tap-create
     * presses, whose click would otherwise re-select).
     */
    suppressTapClick?: boolean;
    /**
     * Swallow the click that follows an Escape-cancelled ACTIVE drag
     * (linking's clip drag/resize). Default false: the tracks let the
     * post-Escape click fall through.
     */
    suppressEscapeClick?: boolean;
}

export interface GestureRoute {
    id: string;
    /** Higher wins. Ties resolve by registration order (stable sort). */
    priority: number;
    /** Claim the press by returning a session, or pass with null. */
    onPress(event: MouseEvent, body: HTMLElement): GestureSession | null;
}

export interface GestureRouter {
    /**
     * Install listeners on a tracks body. MUST be called after
     * detailStrip.attach: the strip's capture-phase chip handler claims chip
     * presses via stopPropagation, which the router honors (see onMouseDown)
     * only when the strip's listener has already run.
     */
    attach(body: HTMLElement): void;
    register(route: GestureRoute): () => void;
    dispose(): void;
}

const DEFAULT_THRESHOLD_PX = 5;

/**
 * A session that claims a press (blocking lower-priority routes) without ever
 * becoming a drag: it never activates, so the concluding click still fires
 * even after mouse movement — used for shift-presses whose shift-CLICK
 * handler does the actual work (retake / audio-span delete).
 */
export const claimOnly = (): GestureSession => ({
    threshold: Number.POSITIVE_INFINITY,
    onMove: () => {},
    onCommit: () => {},
});

export const createGestureRouter = (): GestureRouter => {
    let body: HTMLElement | null = null;
    const routes: GestureRoute[] = [];
    let live: {
        session: GestureSession;
        startX: number;
        startY: number;
        active: boolean;
    } | null = null;
    let swallowNextClick = false;

    const ctxFor = (me: MouseEvent): GestureMoveCtx => ({
        event: me,
        dx: me.clientX - (live?.startX ?? 0),
        dy: me.clientY - (live?.startY ?? 0),
    });

    const onMouseDown = (event: Event): void => {
        // A fresh press voids any pending click swallow.
        swallowNextClick = false;
        const me = event as MouseEvent;
        // An earlier capture listener on this body (the detail strip's chip
        // handler) already stopped propagation to claim the press. stopPropagation
        // kills the old bubble-phase handlers but NOT sibling capture listeners,
        // so the router must honor the stopped flag explicitly.
        if (me.cancelBubble) {
            return;
        }
        if (me.button !== 0 || !(me.target instanceof Element) || !body) {
            return;
        }
        if (live) {
            return;
        }
        const ordered = [...routes].sort((a, b) => b.priority - a.priority);
        for (const route of ordered) {
            const session = route.onPress(me, body);
            if (session) {
                live = {
                    session,
                    startX: me.clientX,
                    startY: me.clientY,
                    active: (session.threshold ?? DEFAULT_THRESHOLD_PX) <= 0,
                };
                // Prevent any bubble handler from double-acting on this claimed press.
                me.stopPropagation();
                return;
            }
        }
    };

    const onDocMouseMove = (event: Event): void => {
        if (!live) {
            return;
        }
        const ctx = ctxFor(event as MouseEvent);
        if (!live.active) {
            const threshold = live.session.threshold ?? DEFAULT_THRESHOLD_PX;
            const dist =
                live.session.axis === "xy"
                    ? Math.hypot(ctx.dx, ctx.dy)
                    : Math.abs(ctx.dx);
            if (dist < threshold) {
                return;
            }
            live.active = true;
        }
        live.session.onMove(ctx);
    };

    const onDocMouseUp = (event: Event): void => {
        if (!live) {
            return;
        }
        const ctx = ctxFor(event as MouseEvent);
        const { session, active } = live;
        live = null;
        if (active) {
            swallowNextClick = true;
            session.onCommit(ctx);
            return;
        }
        if (session.suppressTapClick) {
            swallowNextClick = true;
        }
        session.onTap?.(ctx);
    };

    const onDocKeyDown = (event: Event): void => {
        if ((event as KeyboardEvent).key !== "Escape" || !live) {
            return;
        }
        const { session, active } = live;
        live = null;
        if (session.suppressEscapeClick && active) {
            swallowNextClick = true;
        }
        session.onCancel?.();
    };

    const onBodyClickCapture = (event: Event): void => {
        if (!swallowNextClick) {
            return;
        }
        swallowNextClick = false;
        event.stopPropagation();
        event.preventDefault();
    };

    const removeListeners = (): void => {
        if (body) {
            body.removeEventListener("mousedown", onMouseDown, true);
            body.removeEventListener("click", onBodyClickCapture, true);
        }
        document.removeEventListener("mousemove", onDocMouseMove);
        document.removeEventListener("mouseup", onDocMouseUp);
        document.removeEventListener("keydown", onDocKeyDown);
    };

    const cancelLive = (): void => {
        if (live) {
            const { session } = live;
            live = null;
            session.onCancel?.();
        }
    };

    return {
        attach: (nextBody: HTMLElement): void => {
            if (body === nextBody) {
                return;
            }
            cancelLive();
            removeListeners();
            body = nextBody;
            swallowNextClick = false;
            nextBody.addEventListener("mousedown", onMouseDown, true);
            nextBody.addEventListener("click", onBodyClickCapture, true);
            document.addEventListener("mousemove", onDocMouseMove);
            document.addEventListener("mouseup", onDocMouseUp);
            document.addEventListener("keydown", onDocKeyDown);
        },
        register: (route: GestureRoute): (() => void) => {
            routes.push(route);
            return () => {
                const at = routes.indexOf(route);
                if (at !== -1) {
                    routes.splice(at, 1);
                }
            };
        },
        dispose: (): void => {
            cancelLive();
            removeListeners();
            body = null;
            swallowNextClick = false;
        },
    };
};
