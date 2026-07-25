import { describe, expect, it } from "@jest/globals";

import { createTimelineHistory } from "./timelineHistory";

const make = (initial: string) => {
    let value = initial;
    const history = createTimelineHistory({
        read: () => value,
        write: (next) => {
            value = next;
        },
    });
    history.rebase();
    return {
        history,
        get: () => value,
        set: (next: string) => {
            value = next;
        },
    };
};

describe("timelineHistory rebase", () => {
    it("drops both stacks when the document was replaced under it", () => {
        const { history, get, set } = make("A");
        set("B");
        history.capture();
        set("C");
        history.capture();
        expect(history.undo()).toBe(true); // C -> B, redo now holds C
        expect(get()).toBe("B");
        expect(history.canUndo()).toBe(true);
        expect(history.canRedo()).toBe(true);

        // The carrier is replaced externally / host params are rebuilt.
        set("EXTERNAL");
        history.rebase();

        expect(history.canUndo()).toBe(false);
        expect(history.canRedo()).toBe(false);
        expect(history.undo()).toBe(false);
        expect(history.redo()).toBe(false);
        expect(get()).toBe("EXTERNAL");
    });

    it("keeps the stacks when the same document is re-adopted", () => {
        const { history, get, set } = make("A");
        set("B");
        history.capture();

        history.rebase();

        expect(history.canUndo()).toBe(true);
        expect(history.undo()).toBe(true);
        expect(get()).toBe("A");
    });
});
