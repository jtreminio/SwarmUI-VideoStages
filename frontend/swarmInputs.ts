import {
    parseBase2EditStageIndex,
    ROOT_DIMENSION_MIN,
    ROOT_FPS_MIN,
} from "./constants";
import { utils } from "./utils";

export const getClipsInput = ():
    | HTMLInputElement
    | HTMLTextAreaElement
    | null => {
    const el = document.getElementById("input_videostages");
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
        return el;
    }
    return null;
};

const VIDEOSTAGES_OPENER = "<videostages>";

export const getPromptInput = ():
    | HTMLInputElement
    | HTMLTextAreaElement
    | null => {
    const el = document.getElementById("input_prompt");
    return el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement
        ? el
        : null;
};

export const readVideoStagesSection = (): string => {
    const value = getPromptInput()?.value ?? "";
    const at = value.indexOf(VIDEOSTAGES_OPENER);
    if (at < 0) {
        return "";
    }
    const rest = value.slice(at + VIDEOSTAGES_OPENER.length);
    const stop = rest.indexOf("<");
    return (stop < 0 ? rest : rest.slice(0, stop)).trim();
};

export const writeVideoStagesSection = (json: string, notify = true): void => {
    const el = getPromptInput();
    if (!el) {
        return;
    }
    const escaped = json.replace(/</g, "\\u003c").replace(/>/g, "\\u003e");
    const section = VIDEOSTAGES_OPENER + escaped;
    const prompt = el.value ?? "";
    const at = prompt.indexOf(VIDEOSTAGES_OPENER);
    if (at < 0) {
        const sep = prompt.length === 0 || prompt.endsWith("\n") ? "" : "\n";
        el.value = prompt + sep + section;
    } else {
        const afterOpener = at + VIDEOSTAGES_OPENER.length;
        const rest = prompt.slice(afterOpener);
        const stop = rest.indexOf("<");
        const spanEnd = stop < 0 ? prompt.length : afterOpener + stop;
        el.value = prompt.slice(0, at) + section + prompt.slice(spanEnd);
    }
    if (notify) {
        triggerChangeFor(el);
    }
};

export const ROOT_DIMENSION_WIDTH_INPUT_ID = "input_videostageswidth";
export const ROOT_DIMENSION_HEIGHT_INPUT_ID = "input_videostagesheight";
export const DIMENSIONS_PRESET_SELECT_ID = "input_videostagesdimensions";
export const DIMENSIONS_PRESET_METADATA_INPUT_ID =
    "input_videostagesdimensionsmetadata";
export const ROOT_FPS_INPUT_ID = "input_videostagesfps";

export const getRootDimensionParamInput = (
    field: "width" | "height",
): HTMLInputElement | null =>
    utils.getInputElement(
        field === "width"
            ? ROOT_DIMENSION_WIDTH_INPUT_ID
            : ROOT_DIMENSION_HEIGHT_INPUT_ID,
    );

export const getRootFpsParamInput = (): HTMLInputElement | null =>
    utils.getInputElement(ROOT_FPS_INPUT_ID);

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

export const getRegisteredRootDimension = (
    field: "width" | "height",
): number | null => {
    const input = getRootDimensionParamInput(field);
    if (!input) {
        return null;
    }
    const value = Math.round(utils.toNumber(input.value, 0));
    return value >= ROOT_DIMENSION_MIN ? value : null;
};

export const getRegisteredRootFps = (): number | null => {
    const input = getRootFpsParamInput();
    if (!input) {
        return null;
    }
    const value = Math.round(utils.toNumber(input.value, 0));
    return value >= ROOT_FPS_MIN ? value : null;
};

export const getCoreDimension = (field: "width" | "height"): number | null => {
    const input = getCoreDimensionInput(field);
    if (!input) {
        return null;
    }
    const value = Math.round(utils.toNumber(input.value, 0));
    return value >= ROOT_DIMENSION_MIN ? value : null;
};

export const seedRegisteredDimensionsFromCore = (
    notifyDomChange = true,
): void => {
    const fields: Array<"width" | "height"> = ["width", "height"];
    for (const field of fields) {
        const ourInput = getRootDimensionParamInput(field);
        if (!ourInput) {
            continue;
        }
        const ourValue = Math.round(utils.toNumber(ourInput.value, 0));
        if (ourValue >= ROOT_DIMENSION_MIN) {
            continue;
        }
        const coreValue = getCoreDimension(field);
        if (coreValue === null) {
            continue;
        }
        ourInput.value = `${coreValue}`;
        if (notifyDomChange) {
            triggerChangeFor(ourInput);
        }
    }
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
