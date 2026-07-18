import type { VideoStagesConfig } from "./types";

/** Which component committed a state change; "external" marks host-side edits. */
export type UpdateOrigin =
    | "detail-strip"
    | "linking"
    | "prompt-track"
    | "audio-track"
    | "audio-segment-track"
    | "boundary-track"
    | "references-track"
    | "retake-track"
    | "timeline"
    | "seed"
    | "history"
    | "external";

export type UpdateKind = "commit" | "external";

export interface UpdateMeta {
    origin: UpdateOrigin;
    kind: UpdateKind;
    /**
     * "value-only": a dock value edit that changes data but not panel
     * structure — the dock keeps its DOM (no rebuild) for these commits.
     */
    hint?: "value-only";
    version: number;
}

export type StoreSubscriber = (
    state: VideoStagesConfig,
    meta: UpdateMeta,
) => void;

/**
 * Carrier access injected by persistence.ts. The store never touches the DOM
 * itself; everything it knows about the carriers comes through these.
 */
export interface StoreDeps {
    /**
     * Change token covering everything a cached parse depends on: both
     * carrier values plus the inherited-dims signature (inherited width/
     * height/fps resolve from live core inputs at parse time).
     */
    readToken(): string;
    /** Raw JSON currently in the data param ("" when the input is absent). */
    readDataParam(): string;
    /** Parse serialized data-param JSON + live prompt overlay; null if corrupt. */
    parse(serialized: string): VideoStagesConfig | null;
    /** Config for an empty/absent carrier: inherited dims, zero clips. */
    parseEmpty(): VideoStagesConfig;
    /**
     * Serialize + write BOTH carriers without dispatching host change events.
     * Returns the serialized data-param JSON.
     */
    writeQuiet(state: VideoStagesConfig): string;
    /** Dispatch the deferred host change events for both carriers. */
    notifyHost(): void;
}

export interface TimelineStore {
    /** Deep clone of the canonical model; callers may mutate freely. */
    getState(): VideoStagesConfig;
    /**
     * Commit `state`: write carriers, adopt the re-parsed post-write model as
     * canonical, then (optionally) notify the host and always notify
     * subscribers. Returns the serialized data-param JSON.
     */
    save(
        state: VideoStagesConfig,
        origin: UpdateOrigin,
        notifyDomChange: boolean,
        hint?: UpdateMeta["hint"],
    ): string;
    /**
     * Absorb a carrier change made by someone else (host undo, paste, prompt
     * typing). No-ops and returns false when the live token matches the
     * canonical model — including the reentrant calls our own save's host
     * notification triggers.
     */
    syncFromCarrier(): boolean;
    subscribe(cb: StoreSubscriber): () => void;
    /** Drop the token cache so the next read re-parses (e.g. param rebuild). */
    invalidate(): void;
    version(): number;
    resetForTests(): void;
}

export const createTimelineStore = (deps: StoreDeps): TimelineStore => {
    let canonical: VideoStagesConfig | null = null;
    let cachedToken: string | null = null;
    // The last token subscribers were brought up to date with (via save's
    // commit notification or syncFromCarrier's external notification). Kept
    // SEPARATE from cachedToken: a plain getState() may re-parse a changed
    // carrier (advancing cachedToken) without anyone having rendered it — the
    // next syncFromCarrier() must still see that gap and notify, or an
    // inherited-dims change absorbed by a racing read would never repaint.
    let syncedToken: string | null = null;
    let lastGoodSerialized = "";
    let ver = 0;
    const subscribers = new Set<StoreSubscriber>();

    /** Today's getState read path: live param, else last-good, else empty. */
    const parseCurrent = (): VideoStagesConfig => {
        const serialized = deps.readDataParam() || lastGoodSerialized;
        if (!serialized) {
            return deps.parseEmpty();
        }
        const parsed = deps.parse(serialized);
        if (parsed) {
            lastGoodSerialized = serialized;
            return parsed;
        }
        if (serialized !== lastGoodSerialized && lastGoodSerialized) {
            const fallback = deps.parse(lastGoodSerialized);
            if (fallback) {
                return fallback;
            }
        }
        return deps.parseEmpty();
    };

    const revalidate = (): VideoStagesConfig => {
        const token = deps.readToken();
        if (canonical && token === cachedToken) {
            return canonical;
        }
        canonical = parseCurrent();
        cachedToken = token;
        if (syncedToken === null) {
            // First read adopts the current carrier as the sync baseline —
            // the same "start from whatever is there" the orchestrator's old
            // refresh() did. Later reads never touch syncedToken.
            syncedToken = token;
        }
        return canonical;
    };

    const notify = (meta: UpdateMeta): void => {
        const state = canonical;
        if (!state) {
            return;
        }
        const snapshot = structuredClone(state);
        for (const cb of [...subscribers]) {
            try {
                cb(snapshot, meta);
            } catch {}
        }
    };

    const save = (
        state: VideoStagesConfig,
        origin: UpdateOrigin,
        notifyDomChange: boolean,
        hint?: UpdateMeta["hint"],
    ): string => {
        // Ordering is load-bearing: the host change events dispatched by
        // notifyHost() run our own carrier listeners SYNCHRONOUSLY, and those
        // listeners call syncFromCarrier(). The canonical model and token must
        // already reflect this write by then, or the store would misread its
        // own save as an external change (full dock rebuild, focus loss).
        const serialized = deps.writeQuiet(state);
        lastGoodSerialized = serialized;
        canonical = deps.parse(serialized) ?? structuredClone(state);
        cachedToken = deps.readToken();
        syncedToken = cachedToken;
        ver++;
        if (notifyDomChange) {
            deps.notifyHost();
        }
        notify({ origin, kind: "commit", hint, version: ver });
        return serialized;
    };

    const syncFromCarrier = (): boolean => {
        const token = deps.readToken();
        // Compare against what subscribers have SEEN, not what the cache has
        // parsed — a read may have refreshed the cache without any render.
        if (canonical && syncedToken !== null && token === syncedToken) {
            return false;
        }
        revalidate();
        syncedToken = cachedToken;
        ver++;
        notify({ origin: "external", kind: "external", version: ver });
        return true;
    };

    return {
        getState: (): VideoStagesConfig => structuredClone(revalidate()),
        save,
        syncFromCarrier,
        subscribe: (cb: StoreSubscriber): (() => void) => {
            subscribers.add(cb);
            return () => {
                subscribers.delete(cb);
            };
        },
        invalidate: (): void => {
            cachedToken = null;
        },
        version: (): number => ver,
        resetForTests: (): void => {
            canonical = null;
            cachedToken = null;
            syncedToken = null;
            lastGoodSerialized = "";
            ver = 0;
            subscribers.clear();
        },
    };
};
