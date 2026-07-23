import { upscaleModeForMethod } from "../../architectures/policy";
import {
    buildField,
    buildOptionSelect,
    type OptionSpec,
} from "../../detailWidgets";
import { disableCapabilityControls } from "../capabilityUi";
import type { StagePanelBindings } from "./types";

const UPSCALE_EPSILON = 1e-6;

export const appendStageUpscaleSection = (
    bindings: StagePanelBindings,
    isRefine: boolean,
): void => {
    if (!isRefine) return;
    const { stage, defaults, fields, stageCapabilities, slider, commit } =
        bindings;
    const upscaleState = stageCapabilities.authoringState(
        "upscale",
        stage.upscale !== 1,
    );
    if (!upscaleState.visible) return;

    const supportedMethods: OptionSpec[] = defaults.upscaleMethodValues.flatMap(
        (value, index) =>
            stageCapabilities.upscaleModes.includes(upscaleModeForMethod(value))
                ? [
                      {
                          value,
                          label: defaults.upscaleMethodLabels[index] ?? value,
                      },
                  ]
                : [],
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
    if (!upscaleState.enabled) {
        disableCapabilityControls(upscaleSlider, upscaleState);
        disableCapabilityControls(methodField, upscaleState);
    }
};
