import { parseBase2EditStageIndex } from "./constants";
import { getBase2EditStageRefs } from "./swarmInputs";
import {
    type ImageSourceOption,
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    REF_SOURCE_UPLOAD,
} from "./types";

export const buildImageSourceOptions = (
    currentValue = "",
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
    const selected = `${currentValue || ""}`.trim();
    if (selected && !options.some((option) => option.value === selected)) {
        const isBase2Edit = parseBase2EditStageIndex(selected) != null;
        options.unshift({
            value: selected,
            label: isBase2Edit ? `Missing Base2Edit ${selected}` : selected,
            disabled: isBase2Edit,
        });
    }
    return options;
};

export const resolveImageSourceValue = (
    currentValue: string,
    options: ImageSourceOption[],
): string => {
    const desired = `${currentValue || ""}`;
    if (options.some((option) => option.value === desired)) {
        return desired;
    }
    return REF_SOURCE_REFINER;
};
