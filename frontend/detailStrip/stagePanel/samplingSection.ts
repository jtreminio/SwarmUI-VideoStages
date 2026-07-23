import {
    buildField,
    buildOptionSelect,
    type OptionSpec,
} from "../../detailWidgets";
import { preserveSelectedOption } from "../../selectOption";
import { disableCapabilityControls } from "../capabilityUi";
import type { StagePanelBindings } from "./types";

export const appendStageDenoisingSection = (
    bindings: StagePanelBindings,
    isRefine: boolean,
): void => {
    const { stage, defaults, fields, slider } = bindings;
    fields.appendChild(
        slider(
            "Steps",
            "steps",
            stage.steps,
            defaults.stepsMin,
            defaults.stepsMax,
            defaults.stepsStep,
            (target, value) => {
                target.steps = Math.round(value);
            },
            {
                help:
                    "How many denoising steps this stage runs. More steps can " +
                    "add detail but take longer; there are diminishing returns " +
                    "past the model's sweet spot.",
            },
        ),
    );
    fields.appendChild(
        slider(
            "CFG Scale",
            "cfg",
            stage.cfgScale,
            defaults.cfgScaleMin,
            defaults.cfgScaleMax,
            defaults.cfgScaleStep,
            (target, value) => {
                target.cfgScale = value;
            },
            {
                help:
                    "How strongly generation follows the prompt. Higher sticks " +
                    "closer to the prompt but can look over-cooked; lower is " +
                    "looser and more natural.",
            },
        ),
    );
    if (isRefine) {
        fields.appendChild(
            slider(
                "Control",
                "control",
                stage.control,
                defaults.controlMin,
                defaults.controlMax,
                defaults.controlStep,
                (target, value) => {
                    target.control = value;
                },
                {
                    title: "Regen strength — higher = more of the stage is re-generated",
                    help:
                        "Regeneration strength for this refine stage. Higher " +
                        "re-generates more of the incoming frames (starting " +
                        "step = floor(Steps × (1 − Control))); at 0 the frames " +
                        "pass through untouched.",
                },
            ),
        );
    }
};

export const appendStageSamplerSchedulerSection = ({
    stage,
    defaults,
    fields,
    stageCapabilities,
    commit,
}: StagePanelBindings): void => {
    const persistedSelect = (
        values: string[],
        labels: string[],
        selected: string,
        assign: (value: string) => void,
    ): HTMLSelectElement => {
        const options: OptionSpec[] = values.map((value, index) => ({
            value,
            label: labels[index] ?? value,
        }));
        preserveSelectedOption(options, selected, "start", (value) => ({
            value,
            label: `${value} (unsupported persisted value)`,
            disabled: true,
        }));
        return buildOptionSelect(options, selected, assign);
    };
    const samplerField = buildField(
        "Sampler",
        persistedSelect(
            defaults.samplerValues,
            defaults.samplerLabels,
            `${stage.sampler ?? ""}`,
            (value) =>
                commit((target) => {
                    target.sampler = value;
                }),
        ),
        undefined,
        "The sampling algorithm used to denoise each step. Leave at the " +
            "model default unless you have a reason to change it.",
    );
    const samplerState = stageCapabilities.authoringState("sampler", true);
    if (!samplerState.enabled) {
        disableCapabilityControls(samplerField, samplerState);
    }
    fields.appendChild(samplerField);

    const schedulerField = buildField(
        "Scheduler",
        persistedSelect(
            defaults.schedulerValues,
            defaults.schedulerLabels,
            `${stage.scheduler ?? ""}`,
            (value) =>
                commit((target) => {
                    target.scheduler = value;
                }),
        ),
        undefined,
        "Controls how the noise level is spaced across the steps. Leave at " +
            "the model default unless you have a reason to change it.",
    );
    const schedulerState = stageCapabilities.authoringState("scheduler", true);
    if (!schedulerState.enabled) {
        disableCapabilityControls(schedulerField, schedulerState);
    }
    fields.appendChild(schedulerField);
};
