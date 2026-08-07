import {
    buildAudioTrackSourceOptions,
    isAceStepFunAudioSource,
} from "./audioSource";
import { parseBase2EditStageIndex } from "./constants";
import { canonicalControlNetSource } from "./controlNetSource";
import {
    CONTROLNET_SOURCE_OPTIONS,
    MEDIA_SOURCE_BASE,
    MEDIA_SOURCE_REFINER,
    MEDIA_SOURCE_UPLOAD,
} from "./generatedMediaSource";
import { buildImageSourceOptions } from "./imageSource";
import { preserveSelectedOption, resolveSelectValue } from "./selectOption";
import type { ClipReferenceKind, ImageSourceOption } from "./types";

export const buildClipReferenceSourceOptions = (
    kind: ClipReferenceKind,
    currentValue = "",
): ImageSourceOption[] => {
    if (kind === "image") {
        return buildImageSourceOptions(currentValue, true);
    }
    const options: ImageSourceOption[] = [
        { value: MEDIA_SOURCE_UPLOAD, label: "Upload" },
        ...CONTROLNET_SOURCE_OPTIONS.map((source) => ({
            value: source,
            label: source,
        })),
    ];
    if (kind === "audio") {
        options.push(
            ...buildAudioTrackSourceOptions(currentValue).filter(
                (option) => option.value !== MEDIA_SOURCE_UPLOAD,
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
): string => resolveSelectValue(currentValue, options, MEDIA_SOURCE_UPLOAD);

export const clipReferenceSourceSupportsKind = (
    kind: ClipReferenceKind,
    source: string,
): boolean => {
    const value = `${source ?? ""}`.trim();
    if (value === MEDIA_SOURCE_UPLOAD || canonicalControlNetSource(value)) {
        return true;
    }
    if (kind === "audio") {
        return isAceStepFunAudioSource(value);
    }
    return (
        kind === "image" &&
        (value === MEDIA_SOURCE_BASE ||
            value === MEDIA_SOURCE_REFINER ||
            parseBase2EditStageIndex(value) !== null)
    );
};
