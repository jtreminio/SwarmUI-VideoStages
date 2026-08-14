import {
    H3_TEXT_ENCODER_DEFAULT,
    H3_TEXT_ENCODERS,
    type H3TextEncoder,
} from "./generatedMiniMaxTextEncoder";

export const normalizeH3TextEncoder = (value: unknown): H3TextEncoder =>
    H3_TEXT_ENCODERS.includes(value as H3TextEncoder)
        ? (value as H3TextEncoder)
        : H3_TEXT_ENCODER_DEFAULT;
