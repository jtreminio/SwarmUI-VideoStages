import {
    CONTROLNET_SOURCE_OPTIONS,
    MEDIA_SOURCE_BASE,
    MEDIA_SOURCE_REFINER,
    MEDIA_SOURCE_UPLOAD,
} from "./generatedMediaSource";
import { parseBase2EditStageIndex } from "./mediaSourceSyntax";

import { preserveSelectedOption, resolveSelectValue } from "./selectOption";
import { getBase2EditStageRefs } from "./swarmInputs";
import type { ImageSourceOption } from "./types";

export const buildImageSourceOptions = (
    currentValue = "",
    includeControlNet = false,
): ImageSourceOption[] => {
    const options: ImageSourceOption[] = [
        { value: MEDIA_SOURCE_BASE, label: "Base Output" },
        { value: MEDIA_SOURCE_REFINER, label: "Refiner Output" },
        { value: MEDIA_SOURCE_UPLOAD, label: "Upload" },
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
): string => resolveSelectValue(currentValue, options, MEDIA_SOURCE_REFINER);
