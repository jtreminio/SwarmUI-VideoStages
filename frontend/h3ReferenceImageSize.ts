import {
    H3_REFERENCE_IMAGE_SIZE_DEFAULT,
    H3_REFERENCE_IMAGE_SIZES,
    type H3ReferenceImageSize,
} from "./generatedMiniMaxReferenceImageSize";

export const normalizeH3ReferenceImageSize = (
    value: unknown,
): H3ReferenceImageSize =>
    H3_REFERENCE_IMAGE_SIZES.includes(value as H3ReferenceImageSize)
        ? (value as H3ReferenceImageSize)
        : H3_REFERENCE_IMAGE_SIZE_DEFAULT;
