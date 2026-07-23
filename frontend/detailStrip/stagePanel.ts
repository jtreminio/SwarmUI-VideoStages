import {
    STAGE_CONTROLNET_STRENGTH_MAX,
    STAGE_CONTROLNET_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_STEP,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_STEP,
} from "../constants";
import {
    buildCheckbox,
    buildField,
    buildSelect,
    buildSlider,
    tagFocus,
} from "../detailWidgets";
import { refSourceLabel } from "../timelineDetail";
import type { Clip, RootDefaults, Stage } from "../types";
import type { DetailStripContext } from "./context";
import { buildStageLorasSection } from "./stageLorasPanel";

const UPSCALE_EPSILON = 1e-6;

export const buildStageParamsColumn = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    stageIdx: number,
    stage: Stage,
    defaults: RootDefaults,
): HTMLElement => {
    const column = document.createElement("div");
    column.className = "vst-detail-col vst-detail-params";
    const sourcedStage0 =
        stageIdx === 0 && !!clip.sourceVideo && stage.skipped !== true;
    const isRefine = stageIdx >= 1 || sourcedStage0;

    const commit = (mutate: (target: Stage) => void): void => {
        context.commit((clips) => {
            const target = clips[clipIdx]?.stages[stageIdx];
            if (target) {
                mutate(target);
            }
        });
    };
    const debouncedCommit = (
        key: string,
        mutate: (target: Stage) => void,
    ): void => {
        context.debouncedCommit(key, (clips) => {
            const target = clips[clipIdx]?.stages[stageIdx];
            if (target) {
                mutate(target);
            }
        });
    };
    const slider = (
        label: string,
        focusKey: string,
        value: number,
        min: number,
        max: number,
        step: number,
        assign: (target: Stage, value: number) => void,
        options?: {
            title?: string;
            onValue?: (value: number) => void;
            help?: string;
        },
    ): HTMLElement =>
        tagFocus(
            buildSlider(
                label,
                value,
                min,
                max,
                step,
                (nextValue) => {
                    options?.onValue?.(nextValue);
                    debouncedCommit(focusKey, (target) =>
                        assign(target, nextValue),
                    );
                },
                options?.title || options?.help
                    ? { title: options.title, help: options.help }
                    : undefined,
            ),
            focusKey,
        );
    const select = (
        values: string[],
        labels: string[],
        selected: string,
        assign: (target: Stage, value: string) => void,
    ): HTMLSelectElement =>
        buildSelect(values, labels, selected, (value) =>
            commit((target) => assign(target, value)),
        );

    const fields = document.createElement("div");
    fields.className = "vst-detail-fields";
    const applyMutedStyle = (): void => {
        fields.classList.toggle(
            "vst-stage-fields-muted",
            stage.skipped === true,
        );
    };
    const syncRailChip = (skipped: boolean): void => {
        context
            .getDockEl()
            ?.querySelector<HTMLElement>(
                `.vst-detail-rail-list .vst-stage-tab:nth-child(${stageIdx + 1})`,
            )
            ?.classList.toggle("vst-stage-tab-skipped", skipped);
    };

    column.appendChild(
        buildCheckbox("Skip this stage", stage.skipped === true, (value) => {
            // The store deep-clones at commit time. This one local mutation is
            // used only to repaint the muted fields synchronously.
            stage.skipped = value;
            applyMutedStyle();
            syncRailChip(value);
            commit((target) => {
                target.skipped = value;
            });
        }),
    );
    column.appendChild(fields);
    applyMutedStyle();

    const modelField = buildField(
        "Model",
        select(
            defaults.modelValues,
            defaults.modelLabels,
            `${stage.model ?? ""}`,
            (target, value) => {
                target.model = value;
            },
        ),
        undefined,
        "The video model this stage runs. Later stages can switch to a " +
            "different model to refine or upscale the previous stage's output.",
    );
    modelField.classList.add("vst-detail-span-2");
    fields.appendChild(modelField);
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
        const methodSelect = select(
            defaults.upscaleMethodValues,
            defaults.upscaleMethodLabels,
            `${stage.upscaleMethod ?? ""}`,
            (target, value) => {
                target.upscaleMethod = value;
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
        fields.appendChild(
            slider(
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
            ),
        );
        fields.appendChild(methodField);
        syncMethod(stage.upscale);
    }

    fields.appendChild(
        buildField(
            "Sampler",
            select(
                defaults.samplerValues,
                defaults.samplerLabels,
                `${stage.sampler ?? ""}`,
                (target, value) => {
                    target.sampler = value;
                },
            ),
            undefined,
            "The sampling algorithm used to denoise each step. Leave at the " +
                "model default unless you have a reason to change it.",
        ),
    );
    fields.appendChild(
        buildField(
            "Scheduler",
            select(
                defaults.schedulerValues,
                defaults.schedulerLabels,
                `${stage.scheduler ?? ""}`,
                (target, value) => {
                    target.scheduler = value;
                },
            ),
            undefined,
            "Controls how the noise level is spaced across the steps. Leave at " +
                "the model default unless you have a reason to change it.",
        ),
    );

    if (clip.refs.length > 0) {
        const refsHeader = document.createElement("div");
        refsHeader.className = "vst-detail-sec vst-detail-span-full";
        refsHeader.textContent = "Reference Strengths";
        fields.appendChild(refsHeader);
        const setRefHover = (refIdx: number, on: boolean): void => {
            context
                .getBoundBody()
                ?.querySelector<HTMLElement>(
                    `.vst-refs-mark[data-clip-idx="${clipIdx}"][data-ref-idx="${refIdx}"]`,
                )
                ?.classList.toggle("vst-ref-hover", on);
        };
        clip.refs.forEach((ref, refIdx) => {
            const current =
                refIdx < stage.refStrengths.length
                    ? stage.refStrengths[refIdx]
                    : STAGE_REF_STRENGTH_MAX;
            const refSlider = buildSlider(
                `R${refIdx}`,
                current,
                STAGE_REF_STRENGTH_MIN,
                STAGE_REF_STRENGTH_MAX,
                STAGE_REF_STRENGTH_STEP,
                (value) => {
                    debouncedCommit(`refstrength-${refIdx}`, (target) => {
                        if (refIdx < target.refStrengths.length) {
                            target.refStrengths[refIdx] = value;
                        }
                    });
                },
                {
                    title: `${refSourceLabel(ref.source ?? "")} · frame ${ref.frame ?? 0}${ref.fromEnd ? " (from end)" : ""}`,
                },
            );
            refSlider.classList.add("vst-stage-ref-slider");
            tagFocus(refSlider, `ref-${refIdx}`);
            refSlider.addEventListener("mouseenter", () =>
                setRefHover(refIdx, true),
            );
            refSlider.addEventListener("mouseleave", () =>
                setRefHover(refIdx, false),
            );
            fields.appendChild(refSlider);
        });
    }

    if (clip.icLoras.length > 0) {
        const controlNetSlider = buildSlider(
            "IC-LoRA Guide Strength",
            stage.controlNetStrength,
            STAGE_CONTROLNET_STRENGTH_MIN,
            STAGE_CONTROLNET_STRENGTH_MAX,
            STAGE_CONTROLNET_STRENGTH_STEP,
            (value) => {
                debouncedCommit("controlnet", (target) => {
                    target.controlNetStrength = value;
                });
            },
            {
                help:
                    "How strongly this stage is conditioned by the clip's " +
                    "IC-LoRA drive video/guides. Higher follows the guide more " +
                    "closely; lower gives the model more freedom.",
            },
        );
        tagFocus(controlNetSlider, "controlnet");
        fields.appendChild(controlNetSlider);
    }

    fields.appendChild(
        buildStageLorasSection(context, clipIdx, stageIdx, stage, defaults),
    );
    if (sourcedStage0) {
        const note = document.createElement("p");
        note.className = "vst-detail-note vst-stage-passthrough-note";
        note.textContent =
            "This stage starts from the source footage — Control sets how much is re-generated (0 passes it through).";
        column.insertBefore(note, fields);
    }
    return column;
};
