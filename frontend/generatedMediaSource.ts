// Generated from MediaSource.cs. Do not edit by hand.

export const MEDIA_SOURCE_UPLOAD = "Upload";
export const MEDIA_SOURCE_NATIVE = "Native";
export const MEDIA_SOURCE_INCOMING = "Incoming";
export const MEDIA_SOURCE_CONTROLNET = "ControlNet";
export const MEDIA_SOURCE_ACE_STEP_FUN = "AceStepFun";
export const MEDIA_SOURCE_BASE = "Base";
export const MEDIA_SOURCE_REFINER = "Refiner";

/** The per-slot spellings, in slot order. */
export const CONTROLNET_SOURCE_OPTIONS = [
    "ControlNet 1",
    "ControlNet 2",
    "ControlNet 3",
] as const;

/**
 * The audio-track sources the backend recognises. Anything else an authored
 * document carries normalizes to "Unrecognized" and is dropped at planning.
 */
export type AudioTrackSourceKind =
    | "Upload"
    | "AceStepFun"
    | "Native"
    | "ControlNet"
    | "Unrecognized";
