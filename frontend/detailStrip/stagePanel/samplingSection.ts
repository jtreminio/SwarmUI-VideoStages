import {
    buildField,
    buildOptionSelect,
    type OptionSpec,
} from "../../detailWidgets";
import {
    H3_ATTENTION_WINDOW_FEATURE,
    H3_ATTENTION_WINDOW_MAX_SECONDS,
    H3_ATTENTION_WINDOW_MIN_SECONDS,
    H3_ATTENTION_WINDOW_STEP_SECONDS,
} from "../../h3AttentionWindow";
import { getVideoStagesHostBridge } from "../../host";
import { preserveSelectedOption } from "../../selectOption";
import { disableCapabilityControls } from "../capabilityUi";
import type { StagePanelBindings } from "./types";

const STEPS_SLIDER_MAX = 20;
const CFG_SCALE_SLIDER_MAX = 10;

export const appendStageDenoisingSection = (
    bindings: StagePanelBindings,
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
            { sliderMax: Math.min(STEPS_SLIDER_MAX, defaults.stepsMax) },
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
            { sliderMax: Math.min(CFG_SCALE_SLIDER_MAX, defaults.cfgScaleMax) },
        ),
    );
};

export const appendStageRefinementControls = (
    bindings: StagePanelBindings,
): void => {
    const { stage, defaults, fields, slider } = bindings;

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
    );
    const schedulerState = stageCapabilities.authoringState("scheduler", true);
    if (!schedulerState.enabled) {
        disableCapabilityControls(schedulerField, schedulerState);
    }
    fields.appendChild(schedulerField);
};

export const appendStageAttentionWindow = ({
    context,
    clip,
    stage,
    fields,
    slider,
}: StagePanelBindings): void => {
    if (
        context.authoring().capabilities.forClip(clip).architectureId !==
            "minimax" ||
        !getVideoStagesHostBridge().hasBackendFeature(
            H3_ATTENTION_WINDOW_FEATURE,
        )
    )
        return;
    const attentionWindow = slider(
        "Attention window (s)",
        "h3AttentionWindowSeconds",
        stage.h3AttentionWindowSeconds,
        H3_ATTENTION_WINDOW_MIN_SECONDS,
        H3_ATTENTION_WINDOW_MAX_SECONDS,
        H3_ATTENTION_WINDOW_STEP_SECONDS,
        (target, value) => {
            target.h3AttentionWindowSeconds = value;
        },
        {
            help: "Total centered temporal attention window for this stage. 0 disables JuanAttn. Dense transformer layers remain fixed at 0,9,19,29,39,49.",
        },
    );
    attentionWindow.dataset.vstH3AttentionWindow = "true";
    fields.append(attentionWindow);
};
