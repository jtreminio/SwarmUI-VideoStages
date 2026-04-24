import { VideoStageUtils } from "./Utils";
import {
    parseBase2EditStageIndex,
    ROOT_DIMENSION_MIN,
    ROOT_FPS_MIN,
} from "./constants";

export const getClipsInput = (): HTMLInputElement | null =>
    VideoStageUtils.getInputElement("input_videostages");

export const getRootDimensionParamInput = (
    field: "width" | "height",
): HTMLInputElement | null =>
    VideoStageUtils.getInputElement(
        field === "width" ? "input_vswidth" : "input_vsheight",
    );

export const getRootFpsParamInput = (): HTMLInputElement | null =>
    VideoStageUtils.getInputElement("input_vsfps");

export const getCoreDimensionInput = (
    field: "width" | "height",
): HTMLInputElement | null => {
    const primaryId = field === "width" ? "input_width" : "input_height";
    const fallbackId =
        field === "width"
            ? "input_aspectratiowidth"
            : "input_aspectratioheight";
    return (
        VideoStageUtils.getInputElement(primaryId) ??
        VideoStageUtils.getInputElement(fallbackId)
    );
};

export const getRegisteredRootDimension = (
    field: "width" | "height",
): number | null => {
    const input = getRootDimensionParamInput(field);
    if (!input) {
        return null;
    }
    const value = Math.round(VideoStageUtils.toNumber(input.value, 0));
    return value >= ROOT_DIMENSION_MIN ? value : null;
};

export const getRegisteredRootFps = (): number | null => {
    const input = getRootFpsParamInput();
    if (!input) {
        return null;
    }
    const value = Math.round(VideoStageUtils.toNumber(input.value, 0));
    return value >= ROOT_FPS_MIN ? value : null;
};

export const getCoreDimension = (field: "width" | "height"): number | null => {
    const input = getCoreDimensionInput(field);
    if (!input) {
        return null;
    }
    const value = Math.round(VideoStageUtils.toNumber(input.value, 0));
    return value >= ROOT_DIMENSION_MIN ? value : null;
};

/**
 * Seeds registered Root width/height from core inputs while this slider is
 * still below {@link ROOT_DIMENSION_MIN}; above that, manual values stick.
 */
export const seedRegisteredDimensionsFromCore = (): void => {
    const fields: Array<"width" | "height"> = ["width", "height"];
    for (const field of fields) {
        const ourInput = getRootDimensionParamInput(field);
        if (!ourInput) {
            continue;
        }
        const ourValue = Math.round(
            VideoStageUtils.toNumber(ourInput.value, 0),
        );
        if (ourValue >= ROOT_DIMENSION_MIN) {
            continue;
        }
        const coreValue = getCoreDimension(field);
        if (coreValue === null) {
            continue;
        }
        ourInput.value = `${coreValue}`;
        triggerChangeFor(ourInput);
    }
};

export const getGroupToggle = (): HTMLInputElement | null =>
    VideoStageUtils.getInputElement("input_group_content_videostages_toggle");

export const getRootModelInput = (): HTMLInputElement | null =>
    VideoStageUtils.getInputElement("input_model");

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

    const select = VideoStageUtils.getSelectElement(fallbackSelectId);
    return {
        values: VideoStageUtils.getSelectValues(select),
        labels: VideoStageUtils.getSelectLabels(select),
    };
};

export const isVideoStagesEnabled = (): boolean => {
    const toggler = getGroupToggle();
    return toggler ? toggler.checked : false;
};
