export interface TimelineHistoryDeps {
    read: () => string | null;
    write: (value: string) => void;
    maxDepth?: number;
}

export interface TimelineHistory {
    syncBaseline: () => void;
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
    // Lazily seeded (first capture, or init's syncBaseline): the factory runs
    // at script load, before genpage has built the carrier inputs — an eager
    // read here would snapshot an empty carrier and warn about the missing
    // Data input.
    let last: string | null = null;
    let suppress = false;

    const syncBaseline = (): void => {
        last = deps.read();
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
        syncBaseline,
        capture,
        undo: () => restore(undoStack, redoStack),
        redo: () => restore(redoStack, undoStack),
        canUndo: () => undoStack.length > 0,
        canRedo: () => redoStack.length > 0,
    };
};
