import {
    architectureDescriptor,
    architectureForModel,
    buildArchitectureModelCatalog,
} from "./architectures/catalog";
import { parseBase2EditStageIndex } from "./constants";
import { getVideoStagesHostBridge } from "./host";
import {
    type ClipTextInput,
    extractGlobalPrompt,
    serializeClipPrompts,
} from "./promptSegments";

export const DATA_INPUT_ID = "input_videostages";
let warnedMissingDataInput = false;

export const getPromptInput = ():
    | HTMLInputElement
    | HTMLTextAreaElement
    | null => getVideoStagesHostBridge().getTextInput("input_prompt");

export const getDataInput = ():
    | HTMLInputElement
    | HTMLTextAreaElement
    | null => {
    const el = getVideoStagesHostBridge().getTextInput(DATA_INPUT_ID);
    if (el) {
        return el;
    }
    if (!warnedMissingDataInput) {
        warnedMissingDataInput = true;
        console.warn(
            `VideoStages: Data param input not found (#${DATA_INPUT_ID}).`,
        );
    }
    return null;
};

export const readDataParam = (): string => getDataInput()?.value ?? "";

export const writeDataParam = (json: string): void => {
    const el = getDataInput();
    if (!el) {
        return;
    }
    el.value = json;
};

export const readStateToken = (): string =>
    `${readDataParam()}\x00${getPromptInput()?.value ?? ""}`;

export const writeClipPrompts = (clips: ClipTextInput[]): void => {
    const el = getPromptInput();
    if (!el) {
        return;
    }
    el.value = serializeClipPrompts(el.value ?? "", clips);
};

/**
 * Dispatch the host `change` events for both carriers after a quiet write
 * (store save path). Data param first, then prompt — the order the old
 * notifying writes fired in. The prompt dispatch runs our own carrier
 * listeners synchronously, so callers must only invoke this once the store's
 * canonical model already reflects the written values.
 */
export const notifyCarrierChanged = (): void => {
    const dataEl = getDataInput();
    if (dataEl) {
        getVideoStagesHostBridge().notifyChanged(dataEl);
    }
    const promptEl = getPromptInput();
    if (promptEl) {
        getVideoStagesHostBridge().notifyChanged(promptEl, true);
    }
};

export const readGlobalPrompt = (): string =>
    extractGlobalPrompt(getPromptInput()?.value ?? "");

export const getGroupToggle = (): HTMLInputElement | null =>
    getVideoStagesHostBridge().getInput(
        "input_group_content_videostages_toggle",
    );

export const getRootModelInput = (): HTMLInputElement | null =>
    getVideoStagesHostBridge().getInput("input_model");

export const getBase2EditStageRefs = (): string[] => {
    const snapshot = getVideoStagesHostBridge().getBase2EditRegistry();
    if (!snapshot?.enabled || !Array.isArray(snapshot.refs)) {
        return [];
    }

    const refs = snapshot.refs
        .map((value) => {
            const stageIndex = parseBase2EditStageIndex(`${value ?? ""}`);
            return stageIndex == null ? null : `edit${stageIndex}`;
        })
        .filter((value): value is string => !!value);
    return [...new Set(refs)].sort(
        (left, right) =>
            (parseBase2EditStageIndex(left) ?? 0) -
            (parseBase2EditStageIndex(right) ?? 0),
    );
};

export const isRootTextToVideoModel = (): boolean => {
    const modelName = `${getRootModelInput()?.value ?? ""}`.trim();
    if (!modelName) {
        return false;
    }
    const catalog = buildArchitectureModelCatalog([modelName], [modelName]);
    const architectureId = architectureForModel(catalog, modelName);
    const architecture = architectureDescriptor(catalog, architectureId);
    return (
        architecture?.capabilities.entryModes.includes("text-to-video") ?? false
    );
};

export const getRootGeneratedEntryMode = ():
    | "text-to-video"
    | "image-to-video" =>
    !`${getRootModelInput()?.value ?? ""}`.trim() || isRootTextToVideoModel()
        ? "text-to-video"
        : "image-to-video";

export const getDropdownOptions = (
    paramId: string,
    fallbackSelectId: string,
): { values: string[]; labels: string[] } => {
    const registered = getVideoStagesHostBridge().getParamOptions(paramId);
    if (registered) {
        return registered;
    }

    const bridge = getVideoStagesHostBridge();
    return bridge.getSelectOptions(bridge.getSelect(fallbackSelectId));
};

export const isVideoStagesEnabled = (): boolean => {
    const toggler = getGroupToggle();
    return toggler ? toggler.checked : false;
};

export const setVideoStagesEnabled = (enabled: boolean): void => {
    const toggler = getGroupToggle();
    if (!toggler || toggler.checked === enabled) {
        return;
    }
    toggler.checked = enabled;
    getVideoStagesHostBridge().notifyChanged(toggler);
};
