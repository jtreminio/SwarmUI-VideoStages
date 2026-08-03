import { upscaleModeForMethod } from "../../architectures/policy";
import {
    buildField,
    buildOptionSelect,
    type OptionSpec,
} from "../../detailWidgets";
import type { StagePanelBindings } from "./types";

const UPSCALE_EPSILON = 1e-6;

const latentUpscaleFeature = (
    method: string,
): "latentUpscale" | "latentModelUpscale" | null => {
    const mode = upscaleModeForMethod(method);
    return mode === "latent"
        ? "latentUpscale"
        : mode === "latent-model"
          ? "latentModelUpscale"
          : null;
};

export const appendStageUpscaleSection = (
    bindings: StagePanelBindings,
    isRefine: boolean,
): void => {
    if (!isRefine) return;
    const { context, clip, stage, defaults, fields, slider, commit } = bindings;
    const capabilities = context.authoring().capabilities.forClip(clip);
    const supportedMethods: OptionSpec[] = defaults.upscaleMethodValues.flatMap(
        (value, index) => {
            const feature = latentUpscaleFeature(value);
            return feature === null || capabilities.decision(feature).supported
                ? [
                      {
                          value,
                          label: defaults.upscaleMethodLabels[index] ?? value,
                      },
                  ]
                : [];
        },
    );
    if (
        stage.upscaleMethod &&
        !supportedMethods.some((option) => option.value === stage.upscaleMethod)
    ) {
        supportedMethods.unshift({
            value: stage.upscaleMethod,
            label: `${stage.upscaleMethod} (unsupported persisted value)`,
            disabled: true,
        });
    }
    const methodSelect = buildOptionSelect(
        supportedMethods,
        `${stage.upscaleMethod ?? ""}`,
        (value) => {
            commit((target) => {
                target.upscaleMethod = value;
            });
        },
    );
    const methodField = buildField(
        "Upscale Method",
        methodSelect,
        undefined,
        "How frames are enlarged before this stage refines them. Only " +
            "applies when Upscale is above 1×.",
    );
    methodField.classList.add("vst-detail-span-2");
    const syncMethod = (upscale: number): void => {
        const disabled = Math.abs(upscale - 1) < UPSCALE_EPSILON;
        methodSelect.disabled = disabled;
        methodField.classList.toggle("vst-field-disabled", disabled);
        methodField.title = disabled
            ? "Set Upscale above 1× to choose a method"
            : "";
    };
    const upscaleSlider = slider(
        "Upscale",
        "upscale",
        stage.upscale,
        defaults.upscaleMin,
        defaults.upscaleMax,
        defaults.upscaleStep,
        (target, value) => {
            target.upscale = value;
        },
        {
            onValue: syncMethod,
            help:
                "Resolution multiplier applied to the incoming frames " +
                "before this stage refines them. 1× keeps the size; " +
                "above 1× enlarges using the Upscale Method.",
        },
    );
    fields.append(upscaleSlider, methodField);
    syncMethod(stage.upscale);
};
