import { parseBase2EditStageIndex, ROOT_DIMENSION_MIN } from "./constants";
import {
    type ClipTextInput,
    extractGlobalPrompt,
    serializeClipPrompts,
} from "./promptSegments";
import { utils } from "./utils";

const DATA_INPUT_ID = "input_videostages";
let warnedMissingDataInput = false;

export const getPromptInput = ():
    | HTMLInputElement
    | HTMLTextAreaElement
    | null => {
    const el = document.getElementById("input_prompt");
    return el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement
        ? el
        : null;
};

export const getDataInput = ():
    | HTMLInputElement
    | HTMLTextAreaElement
    | null => {
    const el = document.getElementById(DATA_INPUT_ID);
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
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

export const writeDataParam = (json: string, notify = true): void => {
    const el = getDataInput();
    if (!el) {
        return;
    }
    el.value = json;
    if (notify) {
        triggerChangeFor(el);
    }
};

export const readStateToken = (): string =>
    `${readDataParam()}\x00${getPromptInput()?.value ?? ""}`;

/**
 * Run `fn` with the host prompt tab-complete guard raised, so the synthetic
 * `input` event our programmatic prompt-box writes dispatch (via
 * triggerChangeFor) can never open the keyboard-driven `<`-prefix suggestion
 * popover. The host's `onInput` checks `blockInput` first and bails
 * (prompttools.js:326-329); dispatch is synchronous, so restoring right after
 * leaves genuine user typing unaffected. No-op when the host global is absent
 * (jsdom / extension-only contexts).
 */
const withSuppressedPromptTabComplete = (fn: () => void): void => {
    const tc =
        typeof promptTabComplete !== "undefined" ? promptTabComplete : null;
    if (!tc) {
        fn();
        return;
    }
    const prev = tc.blockInput;
    tc.blockInput = true;
    try {
        fn();
    } finally {
        tc.blockInput = prev;
    }
};

export const writeClipPrompts = (
    clips: ClipTextInput[],
    notify = true,
): void => {
    const el = getPromptInput();
    if (!el) {
        return;
    }
    el.value = serializeClipPrompts(el.value ?? "", clips);
    if (notify) {
        withSuppressedPromptTabComplete(() => triggerChangeFor(el));
    }
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
        triggerChangeFor(dataEl);
    }
    const promptEl = getPromptInput();
    if (promptEl) {
        withSuppressedPromptTabComplete(() => triggerChangeFor(promptEl));
    }
};

export const readGlobalPrompt = (): string =>
    extractGlobalPrompt(getPromptInput()?.value ?? "");

export const readCarrierSnapshot = (): string =>
    JSON.stringify({
        data: readDataParam(),
        prompt: getPromptInput()?.value ?? "",
    });

export const restoreCarrierSnapshot = (snapshot: string): void => {
    let parsed: { data?: unknown; prompt?: unknown };
    try {
        parsed = JSON.parse(snapshot);
    } catch {
        return;
    }
    writeDataParam(typeof parsed.data === "string" ? parsed.data : "", false);
    const el = getPromptInput();
    if (!el) {
        return;
    }
    el.value = typeof parsed.prompt === "string" ? parsed.prompt : "";
    triggerChangeFor(el);
};

export const getCoreDimensionInput = (
    field: "width" | "height",
): HTMLInputElement | null => {
    const primaryId = field === "width" ? "input_width" : "input_height";
    const fallbackId =
        field === "width"
            ? "input_aspectratiowidth"
            : "input_aspectratioheight";
    return (
        utils.getInputElement(primaryId) ?? utils.getInputElement(fallbackId)
    );
};

export const getCoreDimension = (field: "width" | "height"): number | null => {
    const input = getCoreDimensionInput(field);
    if (!input) {
        return null;
    }
    const value = Math.round(utils.toNumber(input.value, 0));
    return value >= ROOT_DIMENSION_MIN ? value : null;
};

export const getGroupToggle = (): HTMLInputElement | null =>
    utils.getInputElement("input_group_content_videostages_toggle");

export const getRootModelInput = (): HTMLInputElement | null =>
    utils.getInputElement("input_model");

export const getBase2EditStageRefs = (): string[] => {
    const snapshot = window.base2editStageRegistry?.getSnapshot?.();
    if (!snapshot?.enabled || !Array.isArray(snapshot.refs)) {
        return [];
    }

    const refs = snapshot.refs
        .map((value) => {
            const stageIndex = parseBase2EditStageIndex(value);
            return stageIndex == null ? null : `edit${stageIndex}`;
        })
        .filter((value): value is string => !!value);
    return [...new Set(refs)].sort(
        (left, right) =>
            (parseBase2EditStageIndex(left) ?? 0) -
            (parseBase2EditStageIndex(right) ?? 0),
    );
};

export const isAvailableBase2EditReference = (value: string): boolean => {
    const stageIndex = parseBase2EditStageIndex(value);
    if (stageIndex == null) {
        return false;
    }
    return getBase2EditStageRefs().includes(`edit${stageIndex}`);
};

export const isRootTextToVideoModel = (): boolean => {
    const modelName = `${getRootModelInput()?.value ?? ""}`.trim();
    if (!modelName) {
        return false;
    }

    if (
        typeof modelsHelpers !== "undefined" &&
        modelsHelpers &&
        typeof modelsHelpers.getDataFor === "function"
    ) {
        const modelData = modelsHelpers.getDataFor(
            "Stable-Diffusion",
            modelName,
        );
        if (modelData?.modelClass?.compatClass?.isText2Video) {
            return true;
        }
    }

    if (
        typeof currentModelHelper !== "undefined" &&
        currentModelHelper &&
        currentModelHelper.curCompatClass &&
        typeof modelsHelpers !== "undefined" &&
        modelsHelpers?.compatClasses
    ) {
        const compatClass =
            modelsHelpers.compatClasses[currentModelHelper.curCompatClass];
        return !!compatClass?.isText2Video;
    }

    return false;
};

export const isImageToVideoWorkflow = (): boolean => {
    if (isRootTextToVideoModel()) {
        return false;
    }
    const videoModel = utils.getSelectElement("input_videomodel");
    return !!`${videoModel?.value ?? ""}`.trim();
};

export const getDropdownOptions = (
    paramId: string,
    fallbackSelectId: string,
): { values: string[]; labels: string[] } => {
    if (typeof getParamById === "function") {
        const param = getParamById(paramId);
        if (
            param?.values &&
            Array.isArray(param.values) &&
            param.values.length > 0
        ) {
            const labels =
                Array.isArray(param.value_names) &&
                param.value_names.length === param.values.length
                    ? [...param.value_names]
                    : [...param.values];
            return { values: [...param.values], labels: labels };
        }
    }

    const select = utils.getSelectElement(fallbackSelectId);
    return {
        values: utils.getSelectValues(select),
        labels: utils.getSelectLabels(select),
    };
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
    triggerChangeFor(toggler);
};
