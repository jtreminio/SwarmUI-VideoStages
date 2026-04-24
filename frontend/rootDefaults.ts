import { ROOT_DIMENSION_MIN } from "./constants";
import {
    getDropdownOptions,
    getRegisteredRootDimension,
    getRegisteredRootFps,
    getRootModelInput,
    isRootTextToVideoModel,
} from "./swarmInputs";
import type { RootDefaults } from "./types";
import { utils } from "./utils";

export const getDefaultStageModel = (modelValues: string[]): string => {
    if (isRootTextToVideoModel()) {
        const modelName = `${getRootModelInput()?.value ?? ""}`.trim();
        if (modelName) {
            return modelName;
        }
    }
    const videoModel =
        `${utils.getSelectElement("input_videomodel")?.value ?? ""}`.trim();
    if (videoModel) {
        return videoModel;
    }
    return modelValues[0] ?? "";
};

export const getRootDefaults = (): RootDefaults => {
    let model = utils.getSelectElement("input_videomodel");
    if ((!model || model.options.length === 0) && isRootTextToVideoModel()) {
        model = utils.getSelectElement("input_model");
    }
    const vae = utils.getSelectElement("input_vae");
    const sampler = getDropdownOptions("sampler", "input_sampler");
    const scheduler = getDropdownOptions("scheduler", "input_scheduler");
    const upscaleMethod = utils.getSelectElement("input_refinerupscalemethod");
    const allUpscaleMethodValues = utils.getSelectValues(upscaleMethod);
    const allUpscaleMethodLabels = utils.getSelectLabels(upscaleMethod);
    const isStageMethod = (value: string): boolean =>
        value.startsWith("pixel-") ||
        value.startsWith("model-") ||
        value.startsWith("latent-") ||
        value.startsWith("latentmodel-");
    const upscaleMethodValues = allUpscaleMethodValues.filter(isStageMethod);
    const upscaleMethodLabels = allUpscaleMethodLabels.filter((_, index) =>
        isStageMethod(allUpscaleMethodValues[index]),
    );

    const fallbackUpscaleMethods = [
        "pixel-lanczos",
        "pixel-bicubic",
        "pixel-area",
        "pixel-bilinear",
        "pixel-nearest-exact",
    ];

    const steps =
        utils.getInputElement("input_videosteps") ??
        utils.getInputElement("input_steps");
    const cfgScale =
        utils.getInputElement("input_videocfg") ??
        utils.getInputElement("input_cfgscale");
    const widthInput =
        utils.getInputElement("input_width") ??
        utils.getInputElement("input_aspectratiowidth");
    const heightInput =
        utils.getInputElement("input_height") ??
        utils.getInputElement("input_aspectratioheight");
    const fpsInput =
        utils.getInputElement("input_videofps") ??
        utils.getInputElement("input_videoframespersecond");
    const framesInput =
        utils.getInputElement("input_videoframes") ??
        utils.getInputElement("input_text2videoframes");

    const fps = Math.max(
        1,
        getRegisteredRootFps() ??
            Math.round(utils.toNumber(fpsInput?.value, 24)),
    );
    const frames = Math.max(
        1,
        Math.round(utils.toNumber(framesInput?.value, 24)),
    );

    return {
        modelValues: utils.getSelectValues(model),
        modelLabels: utils.getSelectLabels(model),
        vaeValues: utils.getSelectValues(vae),
        vaeLabels: utils.getSelectLabels(vae),
        samplerValues: sampler.values,
        samplerLabels: sampler.labels,
        schedulerValues: scheduler.values,
        schedulerLabels: scheduler.labels,
        upscaleMethodValues:
            upscaleMethodValues.length > 0
                ? upscaleMethodValues
                : fallbackUpscaleMethods,
        upscaleMethodLabels:
            upscaleMethodLabels.length > 0
                ? upscaleMethodLabels
                : fallbackUpscaleMethods,
        width:
            getRegisteredRootDimension("width") ??
            Math.max(
                ROOT_DIMENSION_MIN,
                Math.round(utils.toNumber(widthInput?.value, 1024)),
            ),
        height:
            getRegisteredRootDimension("height") ??
            Math.max(
                ROOT_DIMENSION_MIN,
                Math.round(utils.toNumber(heightInput?.value, 1024)),
            ),
        fps,
        frames,
        control: 0.5,
        controlMin: 0.05,
        controlMax: 1,
        controlStep: 0.05,
        upscale: 1,
        upscaleMin: 0.25,
        upscaleMax: 4,
        upscaleStep: 0.25,
        steps: 8,
        stepsMin: Math.max(1, Math.round(utils.toNumber(steps?.min, 1))),
        stepsMax: Math.min(
            50,
            Math.max(1, Math.round(utils.toNumber(steps?.max, 200))),
        ),
        stepsStep: Math.max(1, Math.round(utils.toNumber(steps?.step, 1))),
        cfgScale: 1,
        cfgScaleMin: utils.toNumber(cfgScale?.min, 0),
        cfgScaleMax: Math.min(10, utils.toNumber(cfgScale?.max, 10)),
        cfgScaleStep: utils.toNumber(cfgScale?.step, 0.5),
    };
};
