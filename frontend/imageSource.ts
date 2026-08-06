import { parseBase2EditStageIndex } from "./constants";
import { CONTROLNET_SOURCE_OPTIONS } from "./controlNetSource";
import { preserveSelectedOption, resolveSelectValue } from "./selectOption";
import { getBase2EditStageRefs } from "./swarmInputs";
import {
    type ImageSourceOption,
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    REF_SOURCE_UPLOAD,
} from "./types";

export const buildImageSourceOptions = (
    currentValue = "",
    includeControlNet = false,
): ImageSourceOption[] => {
    const options: ImageSourceOption[] = [
        { value: REF_SOURCE_BASE, label: "Base Output" },
        { value: REF_SOURCE_REFINER, label: "Refiner Output" },
        { value: REF_SOURCE_UPLOAD, label: "Upload" },
    ];
    for (const editRef of getBase2EditStageRefs()) {
        const editStage = parseBase2EditStageIndex(editRef);
        options.push({
            value: editRef,
            label: `Base2Edit Edit ${editStage} Output`,
        });
    }
    if (includeControlNet) {
        for (const source of CONTROLNET_SOURCE_OPTIONS) {
            options.push({ value: source, label: source });
        }
    }
    preserveSelectedOption(options, currentValue, "start", (value) => {
        const isBase2Edit = parseBase2EditStageIndex(value) != null;
        return {
            value,
            label: isBase2Edit ? `Missing Base2Edit ${value}` : value,
            disabled: isBase2Edit,
        };
    });
    return options;
};

export const resolveImageSourceValue = (
    currentValue: string,
    options: ImageSourceOption[],
): string => resolveSelectValue(currentValue, options, REF_SOURCE_REFINER);
