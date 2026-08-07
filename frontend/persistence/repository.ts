import {
    type AuthoringTransactionSnapshot,
    captureAuthoringTransactionSnapshot,
} from "../authoringSnapshot";
import { assignMissingHues } from "../clipColor";
import { ROOT_DIMENSION_STEP } from "../constants";
import { videoStagesDebugLog } from "../debugLog";
import type {
    DocumentCommand,
    DocumentCommandContext,
} from "../documentCommands";
import { diffDocuments } from "../documentDiff";
import { snapExplicitDocumentDimensions } from "../documentDimensionSnap";
import { getVideoStagesHostBridge } from "../host";
import { ensureAuthoringDocumentIdentity } from "../identity";
import {
    createTimelineStore,
    type TimelineDispatchResult,
    type TimelineStore,
    type UpdateOrigin,
} from "../store";
import { isVideoStagesEnabled } from "../swarmInputs";
import type { Clip, VideoStagesConfig } from "../types";
import {
    dataCarrierNeedsCanonicalIdRepair,
    resetTimelineCarrierAdapterForTests,
    timelineCarrierAdapter,
} from "./carrierAdapter";

export interface SaveStateOptions {
    notifyDomChange?: boolean;
    /** Which component committed this save; threaded to store subscribers. */
    origin?: UpdateOrigin;
    /** Allows dock value edits to skip an unnecessary panel rebuild. */
    valueOnly?: boolean;
    /** Optional compare-and-swap guard from TimelineStore.getSnapshot(). */
    expectedRevision?: number;
}

export type DispatchDocumentCommandOptions = SaveStateOptions;

const commandContextFor = (
    transaction: AuthoringTransactionSnapshot,
): DocumentCommandContext => ({
    architectureCatalog: transaction.defaults.modelCatalog,
    generatedEntryMode: transaction.generatedEntryMode,
});

const store = createTimelineStore(timelineCarrierAdapter);

export const getTimelineStore = (): TimelineStore => store;

export const __resetPersistenceForTests = (): void => {
    store.resetForTests();
    resetTimelineCarrierAdapterForTests();
};

export const getState = (): VideoStagesConfig => store.getState();

const throwSaveFailure = (
    phase: "diff" | "dispatch",
    error: unknown,
): never => {
    const detail =
        error instanceof Error
            ? `${error.name}: ${error.message}`
            : String(error);
    console.error(`[VideoStages persistence] saveState ${phase} failed`, error);
    videoStagesDebugLog("persistence", `saveState ${phase} failed`, {
        detail,
    });
    throw new Error(`VideoStages saveState ${phase} failed: ${detail}`, {
        cause: error,
    });
};

const saveRequestedState = (
    requestedInput: VideoStagesConfig,
    options: SaveStateOptions | undefined,
    snapshot = store.getSnapshot(),
): void => {
    const transaction = captureAuthoringTransactionSnapshot();
    const commandContext = commandContextFor(transaction);
    const requested = structuredClone(requestedInput);
    ensureAuthoringDocumentIdentity(requested);
    assignMissingHues(requested.clips);
    const dimensionSnap = snapExplicitDocumentDimensions(
        requested,
        transaction.defaults.modelCatalog,
    );

    const before = structuredClone(snapshot.state);
    ensureAuthoringDocumentIdentity(before);
    assignMissingHues(before.clips);

    const diffCommand = (() => {
        try {
            return diffDocuments(before, requested, commandContext);
        } catch (error) {
            return throwSaveFailure("diff", error);
        }
    })();
    const command =
        diffCommand.commands.length === 0 && dataCarrierNeedsCanonicalIdRepair()
            ? {
                  type: "batch" as const,
                  commands: [
                      {
                          type: "root.patch" as const,
                          patch: { schemaVersion: requested.schemaVersion },
                      },
                  ],
              }
            : diffCommand;

    const willNotifyDom = options?.notifyDomChange !== false;
    const result = store.dispatch(
        command,
        options?.origin ?? "timeline",
        willNotifyDom,
        options?.expectedRevision ?? snapshot.revision,
        options?.valueOnly ? "value-only" : undefined,
        commandContext,
    );
    if (!result.applied) {
        throwSaveFailure("dispatch", result.failure ?? "unknown failure");
    }
    if (dimensionSnap.changed) {
        const gridReason =
            dimensionSnap.multiple > ROOT_DIMENSION_STEP
                ? ` Active architecture features require multiples of ${dimensionSnap.multiple}.`
                : ` VideoStages dimensions use multiples of ${ROOT_DIMENSION_STEP}.`;
        getVideoStagesHostBridge().showError(
            `VideoStages adjusted the timeline resolution from ${dimensionSnap.before.width}×${dimensionSnap.before.height} to ${dimensionSnap.after.width}×${dimensionSnap.after.height}.${gridReason}`,
        );
    }
    videoStagesDebugLog("persistence", "saveState", {
        notifyDomChange: options?.notifyDomChange,
        willNotifyDom,
        commandCount: command.commands.length,
        revision: result.revision,
    });
};

export const saveState = (
    state: VideoStagesConfig,
    options?: SaveStateOptions,
): void => saveRequestedState(state, options);

export const dispatchDocumentCommand = (
    command: DocumentCommand,
    options?: DispatchDocumentCommandOptions,
): TimelineDispatchResult => {
    const transaction = captureAuthoringTransactionSnapshot();
    const willNotifyDom = options?.notifyDomChange !== false;
    const result = store.dispatch(
        command,
        options?.origin ?? "timeline",
        willNotifyDom,
        options?.expectedRevision,
        options?.valueOnly ? "value-only" : undefined,
        commandContextFor(transaction),
    );
    videoStagesDebugLog("persistence", "dispatchDocumentCommand", {
        command: command.type,
        applied: result.applied,
        failure: result.failure,
        revision: result.revision,
        willNotifyDom,
    });
    return result;
};

export const getClips = (): Clip[] => getState().clips;

export const saveClips = (clips: Clip[], options?: SaveStateOptions): void => {
    videoStagesDebugLog("persistence", "saveClips", {
        clipCount: clips.length,
    });
    const snapshot = store.getSnapshot();
    const state = structuredClone(snapshot.state);
    state.clips = structuredClone(clips);
    const notifyDomChange =
        options?.notifyDomChange !== undefined
            ? options.notifyDomChange
            : isVideoStagesEnabled();
    saveRequestedState(state, { ...options, notifyDomChange }, snapshot);
};
