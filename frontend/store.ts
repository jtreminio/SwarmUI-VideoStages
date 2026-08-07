import { videoStagesDebugLog } from "./debugLog";
import {
    type CommandFailure,
    type DocumentCommand,
    type DocumentCommandContext,
    reduceDocumentCommand,
} from "./documentCommands";
import { ensureAuthoringDocumentIdentity } from "./identity";
import type { AuthoringDocument } from "./types";

/** Which component committed a state change; "external" marks host-side edits. */
export type UpdateOrigin =
    | "detail-strip"
    | "linking"
    | "prompt-track"
    | "audio-span-track"
    | "boundary-track"
    | "references-track"
    | "retake-track"
    | "history"
    | "timeline"
    | "external";

export interface UpdateMeta {
    origin: UpdateOrigin;
    /**
     * "value-only": a dock value edit that changes data but not panel
     * structure — the dock keeps its DOM (no rebuild) for these commits.
     */
    hint?: "value-only";
}

export type StoreSubscriber = (
    state: AuthoringDocument,
    meta: UpdateMeta,
) => void;

export interface TimelineStoreSnapshot {
    state: AuthoringDocument;
    /** Monotonic document revision suitable for fail-closed async commands. */
    revision: number;
}

export type DispatchFailure =
    | CommandFailure
    | "stale-revision"
    | "invalid-serialized-state";

export interface TimelineDispatchResult {
    applied: boolean;
    failure?: DispatchFailure;
    revision: number;
}

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
    parse(serialized: string): AuthoringDocument | null;
    /** Config for an empty/absent carrier: inherited dims, zero clips. */
    parseEmpty(): AuthoringDocument;
    /** Produce the exact data-param JSON without mutating either carrier. */
    serialize(state: AuthoringDocument): string;
    /** Write BOTH carriers without dispatching host change events. */
    writeQuiet(state: AuthoringDocument, serialized: string): void;
    /** Mirror a valid external carrier edit into extension-owned storage. */
    writeDurable?(state: AuthoringDocument): void;
    /** Dispatch the deferred host change events for both carriers. */
    notifyHost(): void;
}

export interface TimelineStore {
    /** Deep clone of the canonical model; callers may mutate freely. */
    getState(): AuthoringDocument;
    /** Atomically read an isolated document snapshot and its revision. */
    getSnapshot(): TimelineStoreSnapshot;
    /** Current document revision, revalidating the live carriers first. */
    revision(): number;
    /**
     * Atomically revalidate, optionally compare-and-swap by revision, reduce
     * one stable-ID command, and commit it through the normal carrier path.
     * No mutable document state escapes in the result.
     */
    dispatch(
        command: DocumentCommand,
        origin: UpdateOrigin,
        notifyDomChange: boolean,
        expectedRevision: number | undefined,
        hint: UpdateMeta["hint"] | undefined,
        context: DocumentCommandContext,
    ): TimelineDispatchResult;
    /**
     * Absorb a carrier change made by someone else (host undo, paste, prompt
     * typing). No-ops and returns false when the live token matches the
     * canonical model — including the reentrant calls our own commit's host
     * notification triggers.
     */
    syncFromCarrier(): boolean;
    subscribe(cb: StoreSubscriber): () => void;
    /** Drop the token cache so the next read re-parses (e.g. param rebuild). */
    invalidate(): void;
    resetForTests(): void;
}

export const createTimelineStore = (deps: StoreDeps): TimelineStore => {
    let canonical: AuthoringDocument | null = null;
    let cachedToken: string | null = null;
    // The last token subscribers were brought up to date with (via commit's
    // commit notification or syncFromCarrier's external notification). Kept
    // SEPARATE from cachedToken: a plain getState() may re-parse a changed
    // carrier (advancing cachedToken) without anyone having rendered it — the
    // next syncFromCarrier() must still see that gap and notify, or an
    // inherited-dims change absorbed by a racing read would never repaint.
    let syncedToken: string | null = null;
    let lastGoodSerialized = "";
    let documentRevision = 0;
    const subscribers = new Set<StoreSubscriber>();

    /** getState read path: live param, else last-good, else empty. */
    const parseCurrent = (): AuthoringDocument => {
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

    const revalidate = (): AuthoringDocument => {
        const token = deps.readToken();
        if (canonical && token === cachedToken) {
            return canonical;
        }
        canonical = parseCurrent();
        cachedToken = token;
        documentRevision++;
        if (syncedToken === null) {
            // First read adopts the current carrier as the sync baseline.
            // Later reads never touch syncedToken.
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
            } catch (error) {
                videoStagesDebugLog("store", "subscriber notification failed", {
                    error,
                    origin: meta.origin,
                });
            }
        }
    };

    const commit = (
        state: AuthoringDocument,
        origin: UpdateOrigin,
        notifyDomChange: boolean,
        hint?: UpdateMeta["hint"],
    ): string | null => {
        // Ordering is load-bearing: the host change events dispatched by
        // notifyHost() run our own carrier listeners SYNCHRONOUSLY, and those
        // listeners call syncFromCarrier(). The canonical model and token must
        // already reflect this write by then, or the store would misread its
        // own commit as an external change (full dock rebuild, focus loss).
        let serialized: string;
        try {
            serialized = deps.serialize(state);
        } catch {
            return null;
        }
        // Mutation boundary: prove the exact candidate carrier can be decoded
        // before either carrier is touched.
        if (!deps.parse(serialized)) {
            return null;
        }
        deps.writeQuiet(state, serialized);
        lastGoodSerialized = serialized;
        canonical = deps.parse(serialized);
        if (!canonical) {
            // The exact bytes passed preflight. A post-write failure would
            // indicate an inconsistent carrier adapter, not user data.
            throw new Error(
                "VideoStages carrier adapter rejected a preflighted commit.",
            );
        }
        cachedToken = deps.readToken();
        syncedToken = cachedToken;
        documentRevision++;
        if (notifyDomChange) {
            deps.notifyHost();
        }
        notify({ origin, hint });
        return serialized;
    };

    const dispatch = (
        command: DocumentCommand,
        origin: UpdateOrigin,
        notifyDomChange: boolean,
        expectedRevision: number | undefined,
        hint: UpdateMeta["hint"] | undefined,
        context: DocumentCommandContext,
    ): TimelineDispatchResult => {
        // This is the dispatch's only pre-reduction carrier revalidation.
        const source = structuredClone(revalidate());
        if (
            expectedRevision !== undefined &&
            expectedRevision !== documentRevision
        ) {
            return {
                applied: false,
                failure: "stale-revision",
                revision: documentRevision,
            };
        }

        ensureAuthoringDocumentIdentity(source);
        const reduced = reduceDocumentCommand(source, command, context);
        if (!reduced.applied) {
            return {
                applied: false,
                failure: reduced.failure,
                revision: documentRevision,
            };
        }
        // An empty diff batch is still a successful atomic dispatch, but it
        // must not rewrite carriers, advance the revision, or notify anyone.
        if (command.type === "batch" && command.commands.length === 0) {
            return {
                applied: true,
                revision: documentRevision,
            };
        }

        const serialized = commit(
            reduced.document,
            origin,
            notifyDomChange,
            hint,
        );
        if (serialized === null) {
            return {
                applied: false,
                failure: "invalid-serialized-state",
                revision: documentRevision,
            };
        }
        return {
            applied: true,
            revision: documentRevision,
        };
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
        if (canonical) {
            deps.writeDurable?.(canonical);
        }
        notify({ origin: "external" });
        return true;
    };

    return {
        getState: (): AuthoringDocument => structuredClone(revalidate()),
        getSnapshot: (): TimelineStoreSnapshot => ({
            state: structuredClone(revalidate()),
            revision: documentRevision,
        }),
        revision: (): number => {
            revalidate();
            return documentRevision;
        },
        dispatch,
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
        resetForTests: (): void => {
            canonical = null;
            cachedToken = null;
            syncedToken = null;
            lastGoodSerialized = "";
            documentRevision = 0;
            subscribers.clear();
        },
    };
};
