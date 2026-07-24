import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { createTimelineHostLifecycle } from "./timelineHostLifecycle";

describe("createTimelineHostLifecycle", () => {
    afterEach(() => {
        jest.restoreAllMocks();
        document.body.innerHTML = "";
    });

    it("flushes pending detail edits on page exit and removes the handlers on dispose", () => {
        const flushPending = jest.fn();
        const lifecycle = createTimelineHostLifecycle({
            refresh: jest.fn(),
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
