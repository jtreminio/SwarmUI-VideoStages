import {
    buildAudioTrackSourceOptions,
    isAceStepFunAudioSource,
} from "./audioSource";
import {
    CONTROLNET_SOURCE_OPTIONS,
    MEDIA_SOURCE_BASE,
    MEDIA_SOURCE_PREVIOUS_CLIP,
    MEDIA_SOURCE_REFINER,
    MEDIA_SOURCE_UPLOAD,
} from "./generatedMediaSource";
import { buildImageSourceOptions } from "./imageSource";
import {
    canonicalControlNetSource,
    parseBase2EditStageIndex,
} from "./mediaSourceSyntax";
import { preserveSelectedOption, resolveSelectValue } from "./selectOption";
import type {
    ClipReference,
    ClipReferenceKind,
    ImageSourceOption,
} from "./types";

export const buildClipReferenceSourceOptions = (
    kind: ClipReferenceKind,
    currentValue = "",
    allowPreviousClip = false,
): ImageSourceOption[] => {
    if (kind === "image") {
        return buildImageSourceOptions(currentValue, true);
    }
    const options: ImageSourceOption[] = [
        { value: MEDIA_SOURCE_UPLOAD, label: "Upload" },
        ...(kind === "video" && allowPreviousClip
            ? [
                  {
                      value: MEDIA_SOURCE_PREVIOUS_CLIP,
                      label: "Previous Clip",
                  },
              ]
            : []),
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
    if (kind === "video" && value === MEDIA_SOURCE_PREVIOUS_CLIP) {
        return true;
    }
    return (
        kind === "image" &&
        (value === MEDIA_SOURCE_BASE ||
            value === MEDIA_SOURCE_REFINER ||
            parseBase2EditStageIndex(value) !== null)
    );
};

export const clipReferenceIsUsable = (
    reference: Pick<ClipReference, "kind" | "source" | "uploadedMedia">,
): boolean =>
    clipReferenceSourceSupportsKind(reference.kind, reference.source) &&
    (reference.source.trim() !== MEDIA_SOURCE_UPLOAD ||
        Boolean(reference.uploadedMedia?.data.trim()));
