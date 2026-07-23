/**
 * Architecture-neutral values in the persisted IC-LoRA authoring contract.
 * Architecture-specific presets, weight discovery, and UI composition remain
 * under architectures/ltx2.
 */
export const STAGE_CONTROLNET_STRENGTH_MIN = 0;
export const STAGE_CONTROLNET_STRENGTH_MAX = 1;
export const STAGE_CONTROLNET_STRENGTH_STEP = 0.1;
export const STAGE_CONTROLNET_STRENGTH_DEFAULT = 0.8;

export const IC_LORA_SOURCE_UPLOAD = "Upload";
export const IC_LORA_SOURCE_STAGE_INPUT = "Stage Input";
export const IC_LORA_STAGE_ALL = -1;
export const IC_LORA_STRENGTH_MIN = 0;
export const IC_LORA_STRENGTH_MAX = 2;
export const IC_LORA_STRENGTH_STEP = 0.05;
export const IC_LORA_STRENGTH_DEFAULT = 1;
export const IC_LORA_ATTENTION_MIN = 0;
export const IC_LORA_ATTENTION_MAX = 1;
export const IC_LORA_ATTENTION_STEP = 0.05;
export const IC_LORA_ATTENTION_DEFAULT = 1;
