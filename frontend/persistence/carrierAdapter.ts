import { assignMissingHues } from "../clipColor";
import {
    ensureAuthoringDocumentIdentity,
    ensureClipEntityIdentities,
} from "../identity";
import type { ClipTextInput } from "../promptSegments";
import { parseClipPrompts } from "../promptSegments";
import {
    getDefaultStageModel,
    getRootDefaults,
    readInheritedDimsSignature,
} from "../rootDefaults";
import {
    getDataInput,
    getPromptInput,
    notifyCarrierChanged,
    readDataParam as readHostDataParam,
    readStateToken,
    writeClipPrompts,
    writeDataParam,
} from "../swarmInputs";
import type { Clip, RootDefaults, VideoStagesConfig } from "../types";
import { applyUiState, saveUiState } from "../uiState";
import {
    createRootConfig,
    decodeStoredDocument,
    type InheritedDims,
    resolveRootDims,
    serializeStateForStorage,
    storedDocumentNeedsCanonicalIdRepair,
} from "./documentCodec";
import {
    clearDurableAuthoringState,
    type DurableAuthoringSnapshot,
    loadDurableAuthoringState,
    saveDurableAuthoringState,
} from "./durableAuthoringState";

const BOOT_CARRIER_PROTECTION_MS = 2_000;

let hydrationComplete = false;
let hydratedSnapshot: DurableAuthoringSnapshot | null = null;
let hydratedPromptCarrierValue: string | null = null;
let pendingHydratedPrompts: ClipTextInput[] | null = null;
let protectOverriddenBootCarrier = false;
let bootCarrierProtectionDeadline = 0;

const overlayPromptAndUiState = (clips: Clip[]): void => {
    ensureClipEntityIdentities(clips);
    const { sections, windows } = parseClipPrompts(
        getPromptInput()?.value ?? "",
    );
    for (let index = 0; index < clips.length; index++) {
        clips[index].prompt = sections.get(index) ?? "";
        clips[index].promptWindows = (windows.get(index) ?? []).map(
            (window) => ({
                prompt: window.prompt,
                start: window.start,
                duration: window.duration,
            }),
        );
    }
    applyUiState(clips);
    ensureClipEntityIdentities(clips);
    assignMissingHues(clips);
};

const inheritedDims = (defaults: RootDefaults): InheritedDims => ({
    width: defaults.width,
    height: defaults.height,
    fps: defaults.fps,
});

const parse = (serialized: string): VideoStagesConfig | null => {
    const defaults = getRootDefaults();
    const decoded = decodeStoredDocument(
        serialized,
        inheritedDims(defaults),
        defaults,
        getDefaultStageModel(defaults),
    );
    if (!decoded) return null;
    overlayPromptAndUiState(decoded.clips);
    return createRootConfig(decoded.dims, decoded.clips, decoded.audioTracks);
};

const parseEmpty = (): VideoStagesConfig => {
    const clips: Clip[] = [];
    overlayPromptAndUiState(clips);
    return createRootConfig(
        resolveRootDims(inheritedDims(getRootDefaults()), {}),
        clips,
    );
};

const applyPendingHydratedPrompts = (): void => {
    if (!pendingHydratedPrompts || !getPromptInput()) {
        return;
    }
    writeClipPrompts(pendingHydratedPrompts);
    hydratedPromptCarrierValue = getPromptInput()?.value ?? null;
    pendingHydratedPrompts = null;
};

const restoreDurableSnapshot = (
    snapshot: DurableAuthoringSnapshot,
): boolean => {
    const defaults = getRootDefaults();
    const decoded = decodeStoredDocument(
        snapshot.document,
        inheritedDims(defaults),
        defaults,
        getDefaultStageModel(defaults),
    );
    if (!decoded || snapshot.prompts.length !== decoded.clips.length) {
        return false;
    }
    writeDataParam(snapshot.document);
    hydratedSnapshot = snapshot;
    pendingHydratedPrompts = snapshot.prompts;
    applyPendingHydratedPrompts();
    return true;
};

const writeDurable = (state: VideoStagesConfig): void => {
    const saved = saveDurableAuthoringState(state);
    if (saved) {
        hydratedSnapshot = saved;
    }
};

const releaseBootCarrierProtection = (): void => {
    protectOverriddenBootCarrier = false;
    bootCarrierProtectionDeadline = 0;
};

const ensureHydratedCarrier = (): void => {
    const dataInput = getDataInput();
    if (!dataInput) {
        return;
    }
    if (hydrationComplete) {
        const promptInput = getPromptInput();
        const protectingBootCarrier =
            protectOverriddenBootCarrier &&
            Date.now() <= bootCarrierProtectionDeadline;
        if (!protectingBootCarrier) {
            protectOverriddenBootCarrier = false;
        }
        // SwarmUI restores cookie-backed params asynchronously. The first
        // carrier value we see can be blank, so the later stale value cannot
        // be identified by byte equality. During the short boot window the
        // durable snapshot owns both carriers; normal VideoStages commits
        // explicitly end this protection, and later host/preset changes flow
        // through the regular external-sync path.
        if (
            protectingBootCarrier &&
            hydratedSnapshot &&
            dataInput.value !== hydratedSnapshot.document
        ) {
            restoreDurableSnapshot(hydratedSnapshot);
        }
        if (
            protectingBootCarrier &&
            hydratedSnapshot &&
            promptInput &&
            promptInput.value !== hydratedPromptCarrierValue
        ) {
            pendingHydratedPrompts = hydratedSnapshot.prompts;
        }
        applyPendingHydratedPrompts();
        return;
    }
    hydrationComplete = true;
    const durable = loadDurableAuthoringState();
    if (durable && restoreDurableSnapshot(durable)) {
        protectOverriddenBootCarrier = true;
        bootCarrierProtectionDeadline = Date.now() + BOOT_CARRIER_PROTECTION_MS;
        return;
    }
    if (durable) {
        clearDurableAuthoringState();
    }

    const existing = readHostDataParam();
    if (!existing) {
        return;
    }
    const state = parse(existing);
    if (state) {
        writeDurable(state);
    }
};

const readDataParam = (): string => {
    ensureHydratedCarrier();
    return readHostDataParam();
};

const writeQuiet = (state: VideoStagesConfig, serialized: string): void => {
    releaseBootCarrierProtection();
    ensureAuthoringDocumentIdentity(state);
    assignMissingHues(state.clips);
    writeDataParam(serialized);
    writeClipPrompts(
        state.clips.map((clip) => ({
            prompt: clip.prompt,
            windows: clip.promptWindows,
        })),
    );
    saveUiState(state.clips);
    writeDurable(state);
};

export const timelineCarrierAdapter = {
    readToken: (): string => {
        ensureHydratedCarrier();
        return `${readStateToken()}\x00${readInheritedDimsSignature()}`;
    },
    readDataParam,
    parse,
    parseEmpty,
    serialize: serializeStateForStorage,
    writeQuiet,
    writeDurable,
    notifyHost: notifyCarrierChanged,
};

export const dataCarrierNeedsCanonicalIdRepair = (): boolean =>
    storedDocumentNeedsCanonicalIdRepair(readDataParam());

export const resetTimelineCarrierAdapterForTests = (
    clearDurable = true,
): void => {
    hydrationComplete = false;
    hydratedSnapshot = null;
    hydratedPromptCarrierValue = null;
    pendingHydratedPrompts = null;
    protectOverriddenBootCarrier = false;
    bootCarrierProtectionDeadline = 0;
    if (clearDurable) {
        clearDurableAuthoringState();
    }
};
