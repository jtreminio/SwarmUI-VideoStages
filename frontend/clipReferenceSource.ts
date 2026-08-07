import {
    buildAudioTrackSourceOptions,
    isAceStepFunAudioSource,
} from "./audioSource";
import { parseBase2EditStageIndex } from "./constants";
import {
    CONTROLNET_SOURCE_OPTIONS,
    canonicalControlNetSource,
} from "./controlNetSource";
import { buildImageSourceOptions } from "./imageSource";
import { preserveSelectedOption, resolveSelectValue } from "./selectOption";
import {
    type ClipReferenceKind,
    type ImageSourceOption,
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    REF_SOURCE_UPLOAD,
} from "./types";

export const buildClipReferenceSourceOptions = (
    kind: ClipReferenceKind,
    currentValue = "",
): ImageSourceOption[] => {
    if (kind === "image") {
        return buildImageSourceOptions(currentValue, true);
    }
    const options: ImageSourceOption[] = [
        { value: REF_SOURCE_UPLOAD, label: "Upload" },
        ...CONTROLNET_SOURCE_OPTIONS.map((source) => ({
            value: source,
            label: source,
        })),
    ];
    if (kind === "audio") {
        options.push(
            ...buildAudioTrackSourceOptions(currentValue).filter(
                (option) => option.value !== REF_SOURCE_UPLOAD,
            ),
        );
    }
    preserveSelectedOption(options, currentValue, "start", (value) => ({
        value,
        label: `${value} (unsupported persisted value)`,
    }));
    return options;
};

export const resolveClipReferenceSourceValue = (
    currentValue: string,
    options: ImageSourceOption[],
): string => resolveSelectValue(currentValue, options, REF_SOURCE_UPLOAD);

export const clipReferenceSourceSupportsKind = (
    kind: ClipReferenceKind,
    source: string,
): boolean => {
    const value = `${source ?? ""}`.trim();
    if (value === REF_SOURCE_UPLOAD || canonicalControlNetSource(value)) {
        return true;
    }
    if (kind === "audio") {
        return isAceStepFunAudioSource(value);
    }
    return (
        kind === "image" &&
        (value === REF_SOURCE_BASE ||
            value === REF_SOURCE_REFINER ||
            parseBase2EditStageIndex(value) !== null)
    );
};
