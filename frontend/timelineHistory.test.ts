import { describe, expect, it } from "@jest/globals";
import { createTimelineHistory } from "./timelineHistory";

const make = (initial: string, maxDepth?: number) => {
    let value = initial;
    const history = createTimelineHistory({
        read: () => value,
        write: (v) => {
            value = v;
        },
        maxDepth,
    });
    // The baseline is lazily seeded in real usage too (timeline init calls
    // rebase once the carrier inputs exist); mirror that here.
    history.rebase();
    return {
        history,
        get: () => value,
        set: (v: string) => {
            value = v;
        },
    };
};

describe("timelineHistory", () => {
    it("undoes and redoes a sequence of captured edits", () => {
        const { history, get, set } = make("A");
        set("B");
        history.capture();
        set("C");
        history.capture();

        expect(history.canUndo()).toBe(true);
        expect(history.undo()).toBe(true);
        expect(get()).toBe("B");
        expect(history.undo()).toBe(true);
        expect(get()).toBe("A");
        expect(history.undo()).toBe(false);

        expect(history.redo()).toBe(true);
        expect(get()).toBe("B");
        expect(history.redo()).toBe(true);
        expect(get()).toBe("C");
        expect(history.redo()).toBe(false);
    });

    it("a fresh edit after undo clears the redo branch", () => {
        const { history, get, set } = make("A");
        set("B");
        history.capture();
        history.undo();
        expect(get()).toBe("A");
        expect(history.canRedo()).toBe(true);

        set("D");
        history.capture();
        expect(history.canRedo()).toBe(false);
        expect(history.undo()).toBe(true);
        expect(get()).toBe("A");
    });

    it("does not record the restore write as a new edit", () => {
        const { history, get, set } = make("A");
        set("B");
        history.capture();
        history.undo(); // writes A (suppressed)
        expect(get()).toBe("A");
        history.capture(); // value A == baseline A -> no-op, redo preserved
        expect(history.canRedo()).toBe(true);
        expect(history.redo()).toBe(true);
        expect(get()).toBe("B");
    });

    it("surfaces a failed restore and consumes the unwritable entry", () => {
        // A restore whose write throws used to return false without popping: the caller saw a
        // plain "nothing to undo", and every later undo re-offered the same unwritable snapshot.
        let value = "A";
        let rejectRestore = false;
        const history = createTimelineHistory({
            read: () => value,
            write: (next) => {
                if (rejectRestore) {
                    throw new Error("stale-revision");
                }
                value = next;
            },
        });
        history.rebase();
        value = "B";
        history.capture();
        rejectRestore = true;

        expect(() => history.undo()).toThrow("stale-revision");
        expect(value).toBe("B");
        expect(history.canUndo()).toBe(false);
        expect(history.canRedo()).toBe(false);
    });

    it("caps the undo stack at maxDepth", () => {
        const { history, set } = make("v0", 2);
        for (let i = 1; i <= 5; i++) {
            set(`v${i}`);
            history.capture();
        }
        expect(history.undo()).toBe(true);
        expect(history.undo()).toBe(true);
        expect(history.undo()).toBe(false);
    });
});

describe("timelineHistory construction", () => {
    it("does not read the carrier at construction (seeds lazily)", () => {
        let reads = 0;
        const history = createTimelineHistory({
            read: () => {
                reads++;
                return "A";
            },
            write: () => {},
        });
        expect(reads).toBe(0);
        history.rebase();
        expect(reads).toBe(1);
    });
});
