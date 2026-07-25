export interface TimelineHistoryDeps {
    read: () => string | null;
    write: (value: string) => void;
    maxDepth?: number;
}

export interface TimelineHistory {
    /**
     * Adopt the document that currently sits in the carriers as the baseline.
     * A DIFFERENT document means the one this history describes was replaced
     * (first init, a host parameter rebuild, an external carrier write), so both
     * stacks are dropped with it — an undo entry from another document would
     * otherwise restore over the new one and look legitimate to the
     * revision-checked write. An identical document keeps the stacks.
     */
    rebase: () => void;
    capture: () => void;
    undo: () => boolean;
    redo: () => boolean;
    canUndo: () => boolean;
    canRedo: () => boolean;
}

export const createTimelineHistory = (
    deps: TimelineHistoryDeps,
): TimelineHistory => {
    const max = deps.maxDepth ?? 50;
    const undoStack: string[] = [];
    let redoStack: string[] = [];
    // Lazily seeded (first capture, or init's rebase): the factory runs at
    // script load, before genpage has built the carrier inputs — an eager read
    // here would snapshot an empty carrier and warn about the missing Data
    // input.
    let last: string | null = null;
    let suppress = false;

    const rebase = (): void => {
        const current = deps.read();
        if (current === last) {
            return;
        }
        last = current;
        undoStack.length = 0;
        redoStack = [];
    };

    const capture = (): void => {
        if (suppress) {
            return;
        }
        const current = deps.read();
        if (current === last) {
            return;
        }
        if (last !== null) {
            undoStack.push(last);
            if (undoStack.length > max) {
                undoStack.shift();
            }
            redoStack = [];
        }
        last = current;
    };

    const restore = (from: string[], to: string[]): boolean => {
        if (from.length === 0) {
            return false;
        }
        const current = deps.read() ?? "";
        const target = from[from.length - 1];
        suppress = true;
        try {
            deps.write(target);
        } catch (error) {
            // The entry is unwritable; consume it so the stack advances instead of re-offering the
            // same failing snapshot forever, and surface the failure rather than reporting a
            // successful no-op.
            from.pop();
            throw error;
        } finally {
            suppress = false;
        }
        from.pop();
        to.push(current);
        last = target;
        return true;
    };

    return {
        rebase,
        capture,
        undo: () => restore(undoStack, redoStack),
        redo: () => restore(redoStack, undoStack),
        canUndo: () => undoStack.length > 0,
        canRedo: () => redoStack.length > 0,
    };
};
