import { reconcileArchitectureIncomingIcLoraDrives } from "./architectures/behaviorRegistry";
import { captureAuthoringTransactionSnapshot } from "./authoringSnapshot";
import { getTimelineStore, saveState } from "./persistence/repository";

export const reorderClip = (from: number, to: number): number | null => {
    const { state, revision } = getTimelineStore().getSnapshot();
    if (
        !Number.isInteger(from) ||
        !Number.isInteger(to) ||
        from < 0 ||
        from >= state.clips.length ||
        to < 0 ||
        to >= state.clips.length
    )
        return null;
    if (from === to) return to;
    const [clip] = state.clips.splice(from, 1);
    state.clips.splice(to, 0, clip);
    const { defaults, generatedEntryMode } =
        captureAuthoringTransactionSnapshot();
    reconcileArchitectureIncomingIcLoraDrives(
        state.clips,
        generatedEntryMode,
        defaults.modelCatalog,
    );
    saveState(state, { expectedRevision: revision, origin: "linking" });
    return to;
};
