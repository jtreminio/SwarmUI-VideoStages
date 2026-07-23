import { assignMissingHues } from "../clipColor";
import {
    ensureAuthoringDocumentIdentity,
    ensureClipEntityIdentities,
} from "../identity";
import { parseClipPrompts } from "../promptSegments";
import { getRootDefaults, readInheritedDimsSignature } from "../rootDefaults";
import {
    getPromptInput,
    notifyCarrierChanged,
    readDataParam,
    readStateToken,
    writeClipPrompts,
    writeDataParam,
} from "../swarmInputs";
import type { Clip, VideoStagesConfig } from "../types";
import { applyUiState, saveUiState } from "../uiState";
import {
    createRootConfig,
    decodeStoredDocument,
    type InheritedDims,
    resolveRootDims,
    serializeStateForStorage,
    storedDocumentNeedsCanonicalIdRepair,
} from "./documentCodec";

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

const inheritedDims = (): InheritedDims => {
    const defaults = getRootDefaults();
    return {
        width: defaults.width,
        height: defaults.height,
        fps: defaults.fps,
    };
};

const parse = (serialized: string): VideoStagesConfig | null => {
    const decoded = decodeStoredDocument(serialized, inheritedDims());
    if (!decoded) return null;
    overlayPromptAndUiState(decoded.clips);
    return createRootConfig(decoded.dims, decoded.clips, decoded.audioTracks);
};

const parseEmpty = (): VideoStagesConfig => {
    const clips: Clip[] = [];
    overlayPromptAndUiState(clips);
    return createRootConfig(resolveRootDims(inheritedDims(), {}), clips);
};

const writeQuiet = (state: VideoStagesConfig, serialized: string): void => {
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
};

export const timelineCarrierAdapter = {
    readToken: (): string =>
        `${readStateToken()}\x00${readInheritedDimsSignature()}`,
    readDataParam,
    parse,
    parseEmpty,
    serialize: serializeStateForStorage,
    writeQuiet,
    notifyHost: notifyCarrierChanged,
};

export const dataCarrierNeedsCanonicalIdRepair = (): boolean =>
    storedDocumentNeedsCanonicalIdRepair(readDataParam());
