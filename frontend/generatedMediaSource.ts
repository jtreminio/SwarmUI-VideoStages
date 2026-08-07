// Generated from MediaSource.cs. Do not edit by hand.

export const MEDIA_SOURCE_UPLOAD = "Upload";
export const MEDIA_SOURCE_NATIVE = "Native";
export const MEDIA_SOURCE_CONTROLNET = "ControlNet";
export const MEDIA_SOURCE_ACE_STEP_FUN = "AceStepFun";

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
