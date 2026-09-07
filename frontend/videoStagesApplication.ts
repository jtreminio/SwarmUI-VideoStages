import {
    getArchitectureCatalogSnapshot,
    loadAuthoritativeArchitectureCatalog,
    refreshAuthoritativeArchitectureCatalog,
    subscribeArchitectureCatalog,
} from "./architectures/catalog";
import {
    getState,
    getTimelineStore,
    saveState,
} from "./persistence/repository";
import { isVideoStagesEnabled } from "./swarmInputs";
import { TIMELINE_AUTHORING_SETTINGS_CHANGED } from "./timelineAuthoringSettings";
import { createTimelineHistory } from "./timelineHistory";
import { createTimelineHostLifecycle } from "./timelineHostLifecycle";

export function createVideoStagesApplication(editor: {
    init(): void;
    refresh(): void;
    flushPending(): void;
    dispose(): void;
}) {
    let unsubscribeStore: (() => void) | null = null;
    let unsubscribeCatalog: (() => void) | null = null;
    let historyNeedsRebase = true;
    const ready = () => getArchitectureCatalogSnapshot().catalog !== null;
    const history = createTimelineHistory({
        read: () => JSON.stringify(getState()),
        write: (value) => {
            saveState(JSON.parse(value) as ReturnType<typeof getState>, {
                expectedRevision: getTimelineStore().revision(),
                notifyDomChange: isVideoStagesEnabled(),
                origin: "history",
            });
        },
    });
    const rebaseHistory = (): void => {
        if (ready() && historyNeedsRebase) {
            history.rebase();
            historyNeedsRebase = false;
        }
    };
    const host = createTimelineHostLifecycle({
        refresh: editor.refresh,
        refreshCatalog: refreshAuthoritativeArchitectureCatalog,
        syncFromCarrier: () => {
            if (!ready()) return;
            rebaseHistory();
            getTimelineStore().syncFromCarrier();
        },
        flushPending: () => {
            if (ready()) editor.flushPending();
        },
        undo: () => ready() && history.undo(),
        redo: () => ready() && history.redo(),
    });
    const init = (): void => {
        const store = getTimelineStore();
        store.invalidate();
        historyNeedsRebase = true;
        unsubscribeStore?.();
        unsubscribeStore = store.subscribe(() => history.capture());
        unsubscribeCatalog?.();
        // Invalidate before the editor's catalog subscriber renders with new capabilities.
        unsubscribeCatalog = subscribeArchitectureCatalog((snapshot) => {
            if (snapshot.status === "ready" && snapshot.catalog) {
                store.invalidate();
                historyNeedsRebase = true;
                rebaseHistory();
            }
        });
        rebaseHistory();
        editor.init();
        host.bind();
        window.removeEventListener(
            TIMELINE_AUTHORING_SETTINGS_CHANGED,
            editor.refresh,
        );
        window.addEventListener(
            TIMELINE_AUTHORING_SETTINGS_CHANGED,
            editor.refresh,
        );
        loadAuthoritativeArchitectureCatalog();
    };
    const dispose = (): void => {
        host.dispose();
        window.removeEventListener(
            TIMELINE_AUTHORING_SETTINGS_CHANGED,
            editor.refresh,
        );
        unsubscribeStore?.();
        unsubscribeStore = null;
        unsubscribeCatalog?.();
        unsubscribeCatalog = null;
        editor.dispose();
    };
    return { init, dispose };
}
