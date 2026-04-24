import { ROOT_DIMENSION_MIN } from "./constants";
import {
    getDropdownOptions,
    getRegisteredRootDimension,
    getRegisteredRootFps,
    getRootModelInput,
    isRootTextToVideoModel,
} from "./swarmInputs";
import type { RootDefaults } from "./TypesTemp";
import { VideoStageUtils } from "./UtilsTemp";

export const getDefaultStageModel = (modelValues: string[]): string => {
    if (isRootTextToVideoModel()) {
        const modelName = `${getRootModelInput()?.value ?? ""}`.trim();
        if (modelName) {
            return modelName;
        }
    }
    return modelValues[0] ?? "";
};

export const getRootDefaults = (): RootDefaults => {
    let model = VideoStageUtils.getSelectElement("input_videomodel");
    if ((!model || model.options.length === 0) && isRootTextToVideoModel()) {
        model = VideoStageUtils.getSelectElement("input_model");
    }
    const vae = VideoStageUtils.getSelectElement("input_vae");
    const sampler = getDropdownOptions("sampler", "input_sampler");
    const scheduler = getDropdownOptions("scheduler", "input_scheduler");
    const upscaleMethod = VideoStageUtils.getSelectElement(
        "input_refinerupscalemethod",
    );
    const allUpscaleMethodValues =
        VideoStageUtils.getSelectValues(upscaleMethod);
    const allUpscaleMethodLabels =
        VideoStageUtils.getSelectLabels(upscaleMethod);
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
        VideoStageUtils.getInputElement("input_videosteps") ??
        VideoStageUtils.getInputElement("input_steps");
    const cfgScale =
        VideoStageUtils.getInputElement("input_videocfg") ??
        VideoStageUtils.getInputElement("input_cfgscale");
    const widthInput =
        VideoStageUtils.getInputElement("input_width") ??
        VideoStageUtils.getInputElement("input_aspectratiowidth");
    const heightInput =
        VideoStageUtils.getInputElement("input_height") ??
        VideoStageUtils.getInputElement("input_aspectratioheight");
    const fpsInput =
        VideoStageUtils.getInputElement("input_videofps") ??
        VideoStageUtils.getInputElement("input_videoframespersecond");
    const framesInput =
        VideoStageUtils.getInputElement("input_videoframes") ??
        VideoStageUtils.getInputElement("input_text2videoframes");

    const fps = Math.max(
        1,
        getRegisteredRootFps() ??
            Math.round(VideoStageUtils.toNumber(fpsInput?.value, 24)),
    );
    const frames = Math.max(
        1,
        Math.round(VideoStageUtils.toNumber(framesInput?.value, 24)),
    );

    return {
        modelValues: VideoStageUtils.getSelectValues(model),
        modelLabels: VideoStageUtils.getSelectLabels(model),
        vaeValues: VideoStageUtils.getSelectValues(vae),
        vaeLabels: VideoStageUtils.getSelectLabels(vae),
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
                Math.round(VideoStageUtils.toNumber(widthInput?.value, 1024)),
            ),
        height:
            getRegisteredRootDimension("height") ??
            Math.max(
                ROOT_DIMENSION_MIN,
                Math.round(VideoStageUtils.toNumber(heightInput?.value, 1024)),
            ),
        fps,
        frames,
        control: 1,
        controlMin: 0.05,
        controlMax: 1,
        controlStep: 0.05,
        upscale: 1,
        upscaleMin: 0.25,
        upscaleMax: 4,
        upscaleStep: 0.25,
        steps: 8,
        stepsMin: Math.max(
            1,
            Math.round(VideoStageUtils.toNumber(steps?.min, 1)),
        ),
        stepsMax: Math.min(
            50,
            Math.max(1, Math.round(VideoStageUtils.toNumber(steps?.max, 200))),
        ),
        stepsStep: Math.max(
            1,
            Math.round(VideoStageUtils.toNumber(steps?.step, 1)),
        ),
        cfgScale: 1,
        cfgScaleMin: VideoStageUtils.toNumber(cfgScale?.min, 0),
        cfgScaleMax: Math.min(10, VideoStageUtils.toNumber(cfgScale?.max, 10)),
        cfgScaleStep: VideoStageUtils.toNumber(cfgScale?.step, 0.5),
    };
};
