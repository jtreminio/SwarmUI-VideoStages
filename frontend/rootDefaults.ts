import {
    architectureForModel,
    buildArchitectureModelCatalog,
    supportedArchitectureCatalog,
} from "./architectures/catalog";
import type { VideoArchitectureId } from "./architectures/types";
import { ROOT_DIMENSION_MIN } from "./constants";
import { dimensionsFor } from "./dimensionPresets";
import { getVideoStagesHostBridge } from "./host";
import {
    getDropdownOptions,
    getRootModelInput,
    isRootTextToVideoModel,
} from "./swarmInputs";
import type { RootDefaults } from "./types";
import { toNumber } from "./utils";

const trimDomValue = (el: { value: string } | null | undefined): string =>
    `${el?.value ?? ""}`.trim();

/** Core input ids that carry the inherited dims/fps, in probe order. */
const WIDTH_INPUT_IDS = ["input_width", "input_aspectratiowidth"];
const HEIGHT_INPUT_IDS = ["input_height", "input_aspectratioheight"];
const ASPECT_RATIO_INPUT_ID = "input_aspectratio";
const SIDE_LENGTH_INPUT_ID = "input_sidelength";
const SIDE_LENGTH_TOGGLE_ID = "input_sidelength_toggle";
const rootVideoFpsInput = (): HTMLInputElement | null =>
    getVideoStagesHostBridge().getRootVideoFpsInput();

const firstPresentInput = (...ids: string[]): HTMLInputElement | null => {
    for (let i = 0; i < ids.length; i++) {
        const el = getVideoStagesHostBridge().getInput(ids[i]);
        if (el) {
            return el;
        }
    }
    return null;
};

export const getDefaultStageModel = (
    modelValues: string[],
    architectureId?: VideoArchitectureId,
): string => {
    const catalog = buildArchitectureModelCatalog(modelValues, modelValues);
    const supports = (modelName: string): boolean => {
        const resolved = architectureForModel(catalog, modelName);
        return (
            resolved !== null &&
            (architectureId === undefined || resolved === architectureId)
        );
    };
    if (isRootTextToVideoModel()) {
        const modelName = trimDomValue(getRootModelInput());
        if (modelName && supports(modelName)) {
            return modelName;
        }
    }
    const videoModel = trimDomValue(
        getVideoStagesHostBridge().getSelect("input_videomodel"),
    );
    if (videoModel && supports(videoModel)) {
        return videoModel;
    }
    return (
        modelValues.find((modelName) => supports(modelName)) ??
        modelValues[0] ??
        ""
    );
};

export const readInheritedDimsSignature = (): string => {
    const width = trimDomValue(firstPresentInput(...WIDTH_INPUT_IDS));
    const height = trimDomValue(firstPresentInput(...HEIGHT_INPUT_IDS));
    const aspectRatio = trimDomValue(
        getVideoStagesHostBridge().getSelect(ASPECT_RATIO_INPUT_ID),
    );
    const sideLength = trimDomValue(
        getVideoStagesHostBridge().getInput(SIDE_LENGTH_INPUT_ID),
    );
    const sideLengthToggle = getVideoStagesHostBridge().getInput(
        SIDE_LENGTH_TOGGLE_ID,
    );
    const fps = trimDomValue(rootVideoFpsInput());
    return `${width}|${height}|${aspectRatio}|${sideLength}|${sideLengthToggle?.checked ?? ""}|${fps}`;
};

export const getRootDefaults = (): RootDefaults => {
    let model = getVideoStagesHostBridge().getSelect("input_videomodel");
    if ((!model || model.options.length === 0) && isRootTextToVideoModel()) {
        model = getVideoStagesHostBridge().getSelect("input_model");
    }
    // The host's lora list can lead with a "(None)" sentinel; it's meaningless
    // in our add-a-lora lists and a freshly-added entry seeded with it would be
    // dropped as "no lora" on the next normalize pass.
    const rawLoras = getDropdownOptions("loras", "input_loras");
    const loras = { values: [] as string[], labels: [] as string[] };
    rawLoras.values.forEach((value, i) => {
        if (`${value}`.replace(/\s+/g, "").toLowerCase() !== "(none)") {
            loras.values.push(value);
            loras.labels.push(rawLoras.labels[i] ?? value);
        }
    });
    const sampler = getDropdownOptions("sampler", "input_sampler");
    const scheduler = getDropdownOptions("scheduler", "input_scheduler");
    const upscaleMethod = getVideoStagesHostBridge().getSelect(
        "input_refinerupscalemethod",
    );
    const upscaleMethodValues =
        getVideoStagesHostBridge().getSelectOptions(upscaleMethod).values;
    const upscaleMethodLabels =
        getVideoStagesHostBridge().getSelectOptions(upscaleMethod).labels;
    const modelCatalog = supportedArchitectureCatalog(
        buildArchitectureModelCatalog(
            getVideoStagesHostBridge().getSelectOptions(model).values,
            getVideoStagesHostBridge().getSelectOptions(model).labels,
        ),
    );
    const models = {
        values: modelCatalog.entries.map((entry) => entry.value),
        labels: modelCatalog.entries.map((entry) => entry.label),
    };

    const steps = firstPresentInput("input_videosteps", "input_steps");
    const cfgScale = firstPresentInput("input_videocfg", "input_cfgscale");
    const widthInput = firstPresentInput(...WIDTH_INPUT_IDS);
    const heightInput = firstPresentInput(...HEIGHT_INPUT_IDS);
    const aspectRatioInput = getVideoStagesHostBridge().getSelect(
        ASPECT_RATIO_INPUT_ID,
    );
    const sideLengthInput =
        getVideoStagesHostBridge().getInput(SIDE_LENGTH_INPUT_ID);
    const sideLengthToggle = getVideoStagesHostBridge().getInput(
        SIDE_LENGTH_TOGGLE_ID,
    );
    const fpsInput = rootVideoFpsInput();
    const framesInput = firstPresentInput(
        "input_videoframes",
        "input_text2videoframes",
    );

    const fps = Math.max(1, Math.round(toNumber(fpsInput?.value, 24)));
    const frames = Math.max(1, Math.round(toNumber(framesInput?.value, 24)));
    const hostWidth = Math.max(
        ROOT_DIMENSION_MIN,
        Math.round(toNumber(widthInput?.value, 1024)),
    );
    const hostHeight = Math.max(
        ROOT_DIMENSION_MIN,
        Math.round(toNumber(heightInput?.value, 1024)),
    );
    const aspectRatio = trimDomValue(aspectRatioInput);
    const sideLengthEnabled =
        sideLengthInput !== null &&
        (sideLengthToggle === null || sideLengthToggle.checked);
    const sideLength = sideLengthEnabled
        ? Math.max(
              ROOT_DIMENSION_MIN,
              Math.round(toNumber(sideLengthInput.value, 1024)),
          )
        : null;
    const aspectDimensions =
        aspectRatio && aspectRatio !== "Custom" && sideLength !== null
            ? dimensionsFor(aspectRatio, sideLength)
            : null;

    return {
        modelValues: models.values,
        modelLabels: models.labels,
        modelCatalog,
        loraValues: loras.values,
        loraLabels: loras.labels,
        loraDefaultWeights: loras.values.map((value) =>
            getVideoStagesHostBridge().getLoraDefaultWeight(value),
        ),
        samplerValues: sampler.values,
        samplerLabels: sampler.labels,
        schedulerValues: scheduler.values,
        schedulerLabels: scheduler.labels,
        upscaleMethodValues,
        upscaleMethodLabels,
        width: aspectDimensions?.width ?? hostWidth,
        height: aspectDimensions?.height ?? hostHeight,
        aspectRatio: aspectRatio || undefined,
        sideLength,
        fps,
        frames,
        control: 0.5,
        controlMin: 0,
        controlMax: 1,
        controlStep: 0.05,
        upscale: 1,
        upscaleMin: 0.25,
        upscaleMax: 4,
        upscaleStep: 0.25,
        steps: 8,
        stepsMin: Math.max(1, Math.round(toNumber(steps?.min, 1))),
        stepsMax: Math.min(
            50,
            Math.max(1, Math.round(toNumber(steps?.max, 200))),
        ),
        stepsStep: Math.max(1, Math.round(toNumber(steps?.step, 1))),
        cfgScale: 1,
        cfgScaleMin: toNumber(cfgScale?.min, 0),
        cfgScaleMax: Math.min(10, toNumber(cfgScale?.max, 10)),
        cfgScaleStep: toNumber(cfgScale?.step, 0.5),
    };
};
