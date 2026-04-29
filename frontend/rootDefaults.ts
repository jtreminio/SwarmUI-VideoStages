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

const STAGE_UPSCALE_PREFIXES = ["pixel-", "model-", "latent-", "latentmodel-"];

const isStageUpscaleMethod = (value: string): boolean =>
    STAGE_UPSCALE_PREFIXES.some((prefix) => value.startsWith(prefix));

const trimDomValue = (el: { value: string } | null | undefined): string =>
    `${el?.value ?? ""}`.trim();

const firstPresentInput = (...ids: string[]): HTMLInputElement | null => {
    for (let i = 0; i < ids.length; i++) {
        const el = utils.getInputElement(ids[i]);
        if (el) {
            return el;
        }
    }
    return null;
};

export const getDefaultStageModel = (modelValues: string[]): string => {
    if (isRootTextToVideoModel()) {
        const modelName = trimDomValue(getRootModelInput());
        if (modelName) {
            return modelName;
        }
    }
    const videoModel = trimDomValue(utils.getSelectElement("input_videomodel"));
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
    const loras = getDropdownOptions("loras", "input_loras");
    const sampler = getDropdownOptions("sampler", "input_sampler");
    const scheduler = getDropdownOptions("scheduler", "input_scheduler");
    const upscaleMethod = utils.getSelectElement("input_refinerupscalemethod");
    const allUpscaleMethodValues = utils.getSelectValues(upscaleMethod);
    const allUpscaleMethodLabels = utils.getSelectLabels(upscaleMethod);
    const stageUpscaleValues: string[] = [];
    const stageUpscaleLabels: string[] = [];
    for (let i = 0; i < allUpscaleMethodValues.length; i++) {
        const value = allUpscaleMethodValues[i];
        if (isStageUpscaleMethod(value)) {
            stageUpscaleValues.push(value);
            stageUpscaleLabels.push(allUpscaleMethodLabels[i]);
        }
    }

    const fallbackUpscaleMethods = [
        "pixel-lanczos",
        "pixel-bicubic",
        "pixel-area",
        "pixel-bilinear",
        "pixel-nearest-exact",
    ];

    const steps = firstPresentInput("input_videosteps", "input_steps");
    const cfgScale = firstPresentInput("input_videocfg", "input_cfgscale");
    const widthInput = firstPresentInput(
        "input_width",
        "input_aspectratiowidth",
    );
    const heightInput = firstPresentInput(
        "input_height",
        "input_aspectratioheight",
    );
    const fpsInput = firstPresentInput(
        "input_videofps",
        "input_videoframespersecond",
    );
    const framesInput = firstPresentInput(
        "input_videoframes",
        "input_text2videoframes",
    );

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
        loraValues: loras.values,
        loraLabels: loras.labels,
        vaeValues: utils.getSelectValues(vae),
        vaeLabels: utils.getSelectLabels(vae),
        samplerValues: sampler.values,
        samplerLabels: sampler.labels,
        schedulerValues: scheduler.values,
        schedulerLabels: scheduler.labels,
        upscaleMethodValues:
            stageUpscaleValues.length > 0
                ? stageUpscaleValues
                : fallbackUpscaleMethods,
        upscaleMethodLabels:
            stageUpscaleLabels.length > 0
                ? stageUpscaleLabels
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
