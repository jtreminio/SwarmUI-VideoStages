import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { setVideoStagesHostBridgeForTests } from "./host";
import { createDefaultVideoStagesHostBridge } from "./host/defaultVideoStagesHostBridge";
import { createTimelineHostLifecycle } from "./timelineHostLifecycle";

describe("createTimelineHostLifecycle", () => {
    afterEach(() => {
        setVideoStagesHostBridgeForTests(null);
        jest.restoreAllMocks();
        document.body.innerHTML = "";
    });

    it("re-requests the architecture catalog when the host refreshes params", () => {
        const refreshCatalog = jest.fn();
        const refresh = jest.fn();
        const hooks: (() => unknown)[] = [];
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            addParamRefreshHook: (hook) => {
                hooks.push(hook);
                return () => {
                    hooks.splice(hooks.indexOf(hook), 1);
                };
            },
        });
        const lifecycle = createTimelineHostLifecycle({
            refresh,
            refreshCatalog,
            syncFromCarrier: jest.fn(),
            flushPending: jest.fn(),
            undo: () => false,
            redo: () => false,
        });

        lifecycle.bind();
        expect(hooks).toHaveLength(1);
        hooks[0]();

        expect(refreshCatalog).toHaveBeenCalledTimes(1);
        lifecycle.dispose();
        expect(hooks).toHaveLength(0);
    });

    it("flushes pending detail edits on page exit and removes the handlers on dispose", () => {
        const flushPending = jest.fn();
        const lifecycle = createTimelineHostLifecycle({
            refresh: jest.fn(),
            refreshCatalog: jest.fn(),
            syncFromCarrier: jest.fn(),
            flushPending,
            undo: () => false,
            redo: () => false,
        });

        lifecycle.bind();
        window.dispatchEvent(new Event("pagehide"));
        window.dispatchEvent(new Event("beforeunload"));
        expect(flushPending).toHaveBeenCalledTimes(2);

        lifecycle.dispose();
        window.dispatchEvent(new Event("pagehide"));
        window.dispatchEvent(new Event("beforeunload"));
        expect(flushPending).toHaveBeenCalledTimes(2);
    });
});
