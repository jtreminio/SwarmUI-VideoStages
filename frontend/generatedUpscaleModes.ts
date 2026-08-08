// Generated from StageUpscalePlan.cs. Do not edit by hand.

/** The mode a method with no recognized prefix classifies as. */
export const UPSCALE_MODE_UNSUPPORTED = "unsupported";

/** Method prefix to mode, in the order the classifier tries them. */
export const UPSCALE_METHOD_PREFIXES = [
    ["latentmodel-", "latent-model"],
    ["latent-", "latent"],
    ["pixel-", "pixel"],
    ["model-", "model"],
] as const;
