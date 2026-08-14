// Generated from MiniMaxTextEncoder.cs. Do not edit by hand.

export const H3_TEXT_ENCODER_FEATURE = "clipproj";

/** Every MiniMax H3 text encoder an authored clip may select. */
export const H3_TEXT_ENCODERS = ["default", "8b", "4b"] as const;

export type H3TextEncoder = (typeof H3_TEXT_ENCODERS)[number];

export const H3_TEXT_ENCODER_DEFAULT: H3TextEncoder = "default";
