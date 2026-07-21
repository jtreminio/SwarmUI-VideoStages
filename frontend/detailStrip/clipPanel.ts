import {
    CLIP_DURATION_MAX,
    CLIP_DURATION_MIN,
    clamp,
    IC_LORA_ATTENTION_MAX,
    IC_LORA_ATTENTION_MIN,
    IC_LORA_ATTENTION_STEP,
    IC_LORA_AUTO,
    IC_LORA_SOURCE_STAGE_INPUT,
    IC_LORA_SOURCE_UPLOAD,
    IC_LORA_STAGE_ALL,
    IC_LORA_STRENGTH_MAX,
    IC_LORA_STRENGTH_MIN,
    IC_LORA_STRENGTH_STEP,
    RETAKE_DURATION_STEP,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_MAX,
    RETAKE_STRENGTH_MIN,
    RETAKE_STRENGTH_STEP,
    STAGE_CONTROLNET_STRENGTH_MAX,
    STAGE_CONTROLNET_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_STEP,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_STEP,
} from "../constants";
import {
    buildAddButton,
    buildCheckbox,
    buildField,
    buildGroup,
    buildInstanceRow,
    buildNumber,
    buildOptionSelect,
    buildSelect,
    buildSlider,
    buildStackSection,
    buildUploadRow,
    clampStartLength,
    sectionLabel,
    tagFocus,
} from "../detailWidgets";
import {
    clearIcLoraAutoFailure,
    ensureIcLoraAutoWeights,
    IC_LORA_AUTO_HINT_ATTR,
    icLoraAutoHint,
} from "../icLoraAutoDownload";
import {
    findIcLoraPreset,
    IC_LORA_PRESET_CUSTOM_ID,
    IC_LORA_PRESET_UNION_CONTROL_ID,
    IC_LORA_PRESETS,
    icLoraRepoUrl,
    icLoraTriggerHint,
} from "../icLoraPresets";
import { defaultIcLora, reconcileIcLoraStage } from "../normalization";
import { getRootDefaults } from "../rootDefaults";
import {
    refSourceLabel,
    stageChipLabel,
    stageChipTitle,
} from "../timelineDetail";
import { applyClipDurationResize } from "../timelineEdit";
import type {
    Clip,
    IcLora,
    IcLoraControlType,
    RootDefaults,
    Stage,
    TimelineSelection,
} from "../types";
import type { DetailStripContext } from "./context";

const GROUP_STAGES = "vstdock_stages";
const GROUP_ICLORA = "vstdock_iclora";
const GROUP_RETAKE = "vstdock_retake";
const DURATION_STEP = 0.1;
const UPSCALE_EPSILON = 1e-6;
const LORA_WEIGHT_STEP = 0.05;
const LORA_WEIGHT_DEFAULT = 1;

const buildClipColumn = (
    ctx: DetailStripContext,
    clip: Clip,
    clipIdx: number,
): HTMLElement => {
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-clip";

    const lengthDerived =
        clip.clipLengthFromAudio === true ||
        clip.clipLengthFromControlNet === true;
    const durationInput = buildNumber(
        clip.duration,
        CLIP_DURATION_MIN,
        CLIP_DURATION_MAX,
        DURATION_STEP,
        (value) => {
            ctx.debouncedCommit("duration", (clips) => {
                const target = clips[clipIdx];
                if (target && !lengthDerived) {
                    applyClipDurationResize(target, value, getRootDefaults);
                }
            });
        },
    );
    durationInput.setAttribute("data-vst-focus-key", "duration");
    const durationField = buildField(
        "Duration (s)",
        durationInput,
        lengthDerived ? "(derived from audio/ControlNet source)" : undefined,
    );
    if (lengthDerived) {
        durationInput.disabled = true;
        durationField.classList.add("vst-field-disabled");
    }
    col.appendChild(durationField);

    col.appendChild(
        buildCheckbox("Skip this clip", clip.skipped === true, (value) => {
            ctx.commit((clips) => {
                const target = clips[clipIdx];
                if (target) {
                    target.skipped = value;
                }
            });
        }),
    );

    return col;
};

const buildStageRail = (
    ctx: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    stageIdx: number,
): HTMLElement => {
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-rail";

    const list = document.createElement("div");
    list.className = "vst-detail-rail-list";
    clip.stages.forEach((stage, idx) => {
        const chip = document.createElement("button");
        chip.type = "button";
        chip.className = "vst-chip vst-stage-tab";
        if (idx === stageIdx) {
            chip.classList.add("vst-stage-tab-active");
        }
        if (stage.skipped) {
            chip.classList.add("vst-stage-tab-skipped");
        }
        chip.textContent = stageChipLabel(idx);
        chip.title = `${stageChipTitle(stage, idx)} · click to edit · Shift+click to delete`;
        chip.addEventListener("click", (event) => {
            if (event.shiftKey) {
                ctx.deleteStage(clipIdx, idx);
            } else {
                ctx.selectStage(clipIdx, idx);
            }
        });
        list.appendChild(chip);
    });
    col.appendChild(list);

    const actions = document.createElement("div");
    actions.className = "vst-detail-rail-actions";
    const addBtn = buildAddButton("Add stage", "vst-detail-add-stage", () =>
        ctx.addStage(clipIdx),
    );
    addBtn.title = "Add a refine stage";
    const deleteBtn = document.createElement("button");
    deleteBtn.type = "button";
    deleteBtn.className =
        "basic-button small-button vst-refs-delete vst-detail-rail-btn vst-detail-delete-stage";
    deleteBtn.textContent = "Delete stage";
    deleteBtn.disabled = clip.stages.length <= 1;
    deleteBtn.title = deleteBtn.disabled
        ? "A clip always keeps at least one stage"
        : `Delete stage ${stageChipLabel(stageIdx)}`;
    deleteBtn.addEventListener("click", (event) => {
        event.preventDefault();
        ctx.deleteStage(clipIdx, stageIdx);
    });
    actions.append(addBtn, deleteBtn);
    col.appendChild(actions);
    return col;
};

const buildParamsColumn = (
    ctx: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    stageIdx: number,
    stage: Stage,
    defaults: RootDefaults,
): { col: HTMLElement; railSkipSync: (skipped: boolean) => void } => {
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-params";
    const isRefine = stageIdx >= 1;

    const stageCommit = (mutate: (target: Stage) => void): void => {
        ctx.commit((clips) => {
            const target = clips[clipIdx]?.stages[stageIdx];
            if (target) {
                mutate(target);
            }
        });
    };
    const stageDebounced = (
        key: string,
        mutate: (target: Stage) => void,
    ): void => {
        ctx.debouncedCommit(key, (clips) => {
            const target = clips[clipIdx]?.stages[stageIdx];
            if (target) {
                mutate(target);
            }
        });
    };
    const stageSlider = (
        label: string,
        focusKey: string,
        value: number,
        min: number,
        max: number,
        step: number,
        assign: (target: Stage, value: number) => void,
        opts?: { title?: string; onValue?: (value: number) => void },
    ): HTMLElement =>
        tagFocus(
            buildSlider(
                label,
                value,
                min,
                max,
                step,
                (v) => {
                    opts?.onValue?.(v);
                    stageDebounced(focusKey, (target) => assign(target, v));
                },
                opts?.title ? { title: opts.title } : undefined,
            ),
            focusKey,
        );
    const stageSelect = (
        values: string[],
        labels: string[],
        selected: string,
        assign: (target: Stage, value: string) => void,
    ): HTMLSelectElement =>
        buildSelect(values, labels, selected, (v) =>
            stageCommit((target) => assign(target, v)),
        );

    const fields = document.createElement("div");
    fields.className = "vst-detail-fields";
    const applyMute = (): void => {
        fields.classList.toggle(
            "vst-stage-fields-muted",
            stage.skipped === true,
        );
    };

    let railSkipSync: (skipped: boolean) => void = () => {};
    col.appendChild(
        buildCheckbox("Skip this stage", stage.skipped === true, (value) => {
            /**
             * Mutating the render snapshot before the commit is safe
             * (the store deep-clones) but deliberate here only: the
             * mute/rail sync reads it synchronously. Don't copy this
             * pattern — go through stageCommit alone.
             */
            stage.skipped = value;
            applyMute();
            railSkipSync(value);
            stageCommit((target) => {
                target.skipped = value;
            });
        }),
    );
    col.appendChild(fields);
    applyMute();

    const modelField = buildField(
        "Model",
        stageSelect(
            defaults.modelValues,
            defaults.modelLabels,
            `${stage.model ?? ""}`,
            (target, value) => {
                target.model = value;
            },
        ),
    );
    modelField.classList.add("vst-detail-span-2");
    fields.appendChild(modelField);

    fields.appendChild(
        stageSlider(
            "Steps",
            "steps",
            stage.steps,
            defaults.stepsMin,
            defaults.stepsMax,
            defaults.stepsStep,
            (target, value) => {
                target.steps = Math.round(value);
            },
        ),
    );
    fields.appendChild(
        stageSlider(
            "CFG Scale",
            "cfg",
            stage.cfgScale,
            defaults.cfgScaleMin,
            defaults.cfgScaleMax,
            defaults.cfgScaleStep,
            (target, value) => {
                target.cfgScale = value;
            },
        ),
    );

    if (isRefine) {
        fields.appendChild(
            stageSlider(
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
                },
            ),
        );
        const methodSelect = stageSelect(
            defaults.upscaleMethodValues,
            defaults.upscaleMethodLabels,
            `${stage.upscaleMethod ?? ""}`,
            (target, value) => {
                target.upscaleMethod = value;
            },
        );
        const methodField = buildField("Upscale Method", methodSelect);
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
            stageSlider(
                "Upscale",
                "upscale",
                stage.upscale,
                defaults.upscaleMin,
                defaults.upscaleMax,
                defaults.upscaleStep,
                (target, value) => {
                    target.upscale = value;
                },
                { onValue: syncMethod },
            ),
        );
        fields.appendChild(methodField);
        syncMethod(stage.upscale);
    }

    fields.appendChild(
        buildField(
            "Sampler",
            stageSelect(
                defaults.samplerValues,
                defaults.samplerLabels,
                `${stage.sampler ?? ""}`,
                (target, value) => {
                    target.sampler = value;
                },
            ),
        ),
    );
    fields.appendChild(
        buildField(
            "Scheduler",
            stageSelect(
                defaults.schedulerValues,
                defaults.schedulerLabels,
                `${stage.scheduler ?? ""}`,
                (target, value) => {
                    target.scheduler = value;
                },
            ),
        ),
    );

    if (clip.refs.length > 0) {
        const refsHeader = document.createElement("div");
        refsHeader.className = "vst-detail-sec vst-detail-span-full";
        refsHeader.textContent = "Reference Strengths";
        fields.appendChild(refsHeader);
        const setRefHover = (refIdx: number, on: boolean): void => {
            ctx.getBoundBody()
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
            const slider = buildSlider(
                `R${refIdx}`,
                current,
                STAGE_REF_STRENGTH_MIN,
                STAGE_REF_STRENGTH_MAX,
                STAGE_REF_STRENGTH_STEP,
                (value) => {
                    stageDebounced(`refstrength-${refIdx}`, (target) => {
                        if (refIdx < target.refStrengths.length) {
                            target.refStrengths[refIdx] = value;
                        }
                    });
                },
                {
                    title: `${refSourceLabel(ref.source ?? "")} · frame ${ref.frame ?? 0}${ref.fromEnd ? " (from end)" : ""}`,
                },
            );
            slider.classList.add("vst-stage-ref-slider");
            tagFocus(slider, `ref-${refIdx}`);
            slider.addEventListener("mouseenter", () =>
                setRefHover(refIdx, true),
            );
            slider.addEventListener("mouseleave", () =>
                setRefHover(refIdx, false),
            );
            fields.appendChild(slider);
        });
    }

    /**
     * Guide Strength is the per-stage conditioning strength shared by every
     * IC-LoRA guide on the clip; only shown when the clip has IC-LoRAs.
     */
    if (clip.icLoras.length > 0) {
        const controlNetSlider = buildSlider(
            "IC-LoRA Guide Strength",
            stage.controlNetStrength,
            STAGE_CONTROLNET_STRENGTH_MIN,
            STAGE_CONTROLNET_STRENGTH_MAX,
            STAGE_CONTROLNET_STRENGTH_STEP,
            (value) => {
                stageDebounced("controlnet", (target) => {
                    target.controlNetStrength = value;
                });
            },
            { hint: "Drive-video conditioning strength for this stage" },
        );
        tagFocus(controlNetSlider, "controlnet");
        fields.appendChild(controlNetSlider);
    }

    fields.appendChild(
        buildLorasSection(ctx, clipIdx, stageIdx, stage, defaults),
    );

    railSkipSync = (skipped: boolean): void => {
        const railChip = ctx
            .getDockEl()
            ?.querySelector<HTMLElement>(
                `.vst-detail-rail-list .vst-stage-tab:nth-child(${stageIdx + 1})`,
            );
        railChip?.classList.toggle("vst-stage-tab-skipped", skipped);
    };

    return { col, railSkipSync };
};

const buildLorasSection = (
    ctx: DetailStripContext,
    clipIdx: number,
    stageIdx: number,
    stage: Stage,
    defaults: RootDefaults,
): HTMLElement => {
    const section = document.createElement("div");
    section.className = "vst-audio-field vst-stage-loras vst-detail-span-full";
    const label = document.createElement("div");
    label.className = "vst-detail-sec";
    label.textContent = `LoRAs — Stage ${stageChipLabel(stageIdx)}`;
    section.appendChild(label);

    if (defaults.loraValues.length === 0) {
        const empty = document.createElement("small");
        empty.className = "vst-audio-field-hint";
        empty.textContent = "(no LoRAs available)";
        section.appendChild(empty);
    } else {
        const list = document.createElement("div");
        list.className = "vst-stage-lora-list";
        stage.loras.forEach((lora, index) => {
            const row = document.createElement("div");
            row.className = "vst-stage-lora-row";
            const select = buildSelect(
                defaults.loraValues,
                defaults.loraLabels,
                lora.name,
                (value) => {
                    ctx.commit((clips) => {
                        const target = clips[clipIdx]?.stages[stageIdx];
                        const entry = target?.loras[index];
                        if (entry) {
                            entry.name = value;
                        }
                    });
                },
            );
            const weight = buildNumber(
                lora.weight,
                -10,
                10,
                LORA_WEIGHT_STEP,
                (value) => {
                    ctx.debouncedCommit(`lora-${index}-weight`, (clips) => {
                        const target = clips[clipIdx]?.stages[stageIdx];
                        const entry = target?.loras[index];
                        if (entry) {
                            entry.weight = value;
                        }
                    });
                },
            );
            weight.classList.add("vst-stage-lora-weight");
            weight.setAttribute("data-vst-focus-key", `lora-${index}-weight`);
            const remove = document.createElement("button");
            remove.type = "button";
            remove.className = "basic-button vst-stage-lora-remove";
            remove.textContent = "×";
            remove.title = "Remove this LoRA";
            remove.addEventListener("click", () => {
                ctx.structuralCommit((clips) => {
                    const target = clips[clipIdx]?.stages[stageIdx];
                    if (!target) {
                        return null;
                    }
                    target.loras.splice(index, 1);
                    return "render";
                });
            });
            row.append(select, weight, remove);
            list.appendChild(row);
        });
        section.appendChild(list);

        const addBtn = document.createElement("button");
        addBtn.type = "button";
        addBtn.className = "basic-button small-button vst-stage-lora-add";
        addBtn.textContent = "+ Add LoRA";
        addBtn.addEventListener("click", () => {
            ctx.structuralCommit((clips) => {
                const target = clips[clipIdx]?.stages[stageIdx];
                if (!target) {
                    return null;
                }
                target.loras.push({
                    name: defaults.loraValues[0] ?? "",
                    weight: LORA_WEIGHT_DEFAULT,
                });
                return "render";
            });
        });
        section.appendChild(addBtn);
    }

    return section;
};

/**
 * Retake section of the clip panel — lives alongside Stages, same shape:
 * a plain section label plus the fields (or an add button when none).
 */
const buildRetakeSection = (
    ctx: DetailStripContext,
    clip: Clip,
    clipIdx: number,
): HTMLElement => {
    const { wrap, col } = buildStackSection("Retake", "vst-detail-retake-col");
    const retake = clip.retake;

    if (!retake) {
        const hint = document.createElement("small");
        hint.className = "vst-audio-field-hint";
        hint.textContent =
            "Regenerates a sub-range when refining a base video.";
        col.append(
            hint,
            buildAddButton("Add retake", "vst-detail-add-retake", () =>
                ctx.createRetake(clipIdx),
            ),
        );
        return wrap;
    }

    const clipDur = Math.max(RETAKE_MIN_DURATION, clip.duration || 0);
    const clampRetake = (
        start: number,
        length: number,
    ): { start: number; length: number } =>
        clampStartLength(start, length, clipDur, RETAKE_MIN_DURATION);

    const startInput = ctx.buildClampedNumber({
        key: "retake-start",
        value: retake.startSeconds,
        min: 0,
        max: Math.max(0, clipDur - RETAKE_MIN_DURATION),
        step: RETAKE_DURATION_STEP,
        readBack: (cs) => cs[clipIdx]?.retake?.startSeconds ?? null,
        mutate: (cs, value) => {
            const r = cs[clipIdx]?.retake;
            if (r) {
                const next = clampRetake(value, r.lengthSeconds);
                r.startSeconds = next.start;
                r.lengthSeconds = next.length;
            }
        },
    });
    col.appendChild(buildField("Start (s)", startInput));

    const lengthInput = ctx.buildClampedNumber({
        key: "retake-length",
        value: retake.lengthSeconds,
        min: RETAKE_MIN_DURATION,
        max: clipDur,
        step: RETAKE_DURATION_STEP,
        readBack: (cs) => cs[clipIdx]?.retake?.lengthSeconds ?? null,
        mutate: (cs, value) => {
            const r = cs[clipIdx]?.retake;
            if (r) {
                const next = clampRetake(r.startSeconds, value);
                r.startSeconds = next.start;
                r.lengthSeconds = next.length;
            }
        },
    });
    col.appendChild(buildField("Length (s)", lengthInput));

    col.appendChild(
        buildSlider(
            "Strength",
            retake.strength,
            RETAKE_STRENGTH_MIN,
            RETAKE_STRENGTH_MAX,
            RETAKE_STRENGTH_STEP,
            (value) => {
                ctx.debouncedCommit("retake-strength", (cs) => {
                    const r = cs[clipIdx]?.retake;
                    if (r) {
                        r.strength = clamp(
                            value,
                            RETAKE_STRENGTH_MIN,
                            RETAKE_STRENGTH_MAX,
                        );
                    }
                });
            },
        ),
    );

    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent =
        "Applies when refining a base video; audio inside the window regenerates with the frames.";
    col.appendChild(note);

    const del = document.createElement("button");
    del.type = "button";
    del.className =
        "basic-button small-button vst-refs-delete vst-detail-delete vst-detail-rail-btn";
    del.textContent = "Remove retake";
    del.addEventListener("click", (event) => {
        event.preventDefault();
        ctx.removeRetake(clipIdx);
    });
    col.appendChild(del);
    return wrap;
};

/**
 * IC-LoRAs section of the clip panel — same shape as Retake: a section
 * label plus stacked per-entry editors. Each entry pairs an in-context LoRA
 * with an optional uploaded drive video; presets seed strength/control-type
 * defaults and surface a trigger-phrase hint, nothing more.
 */
const buildIcLorasSection = (
    ctx: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    defaults: RootDefaults,
): HTMLElement => {
    const { wrap, col } = buildStackSection(
        "IC-LoRAs",
        "vst-detail-iclora-col",
    );

    const entryField = (clips: Clip[], entryIdx: number): IcLora | undefined =>
        clips[clipIdx]?.icLoras[entryIdx];

    clip.icLoras.forEach((entry, entryIdx) => {
        const { row, fields } = buildInstanceRow({
            rowClass: "vst-detail-iclora",
            indexAttr: "data-vst-iclora-idx",
            index: entryIdx,
            active: false,
            title: `IC-LoRA ${entryIdx + 1}`,
            deleteLabel: "Remove",
            onDelete: () => {
                ctx.structuralCommit((clips) => {
                    const target = clips[clipIdx];
                    if (!target || entryIdx >= target.icLoras.length) {
                        return null;
                    }
                    target.icLoras.splice(entryIdx, 1);
                    return "render";
                });
            },
            repoint: () => {},
        });

        const presetSelect = buildOptionSelect(
            [
                { value: IC_LORA_PRESET_CUSTOM_ID, label: "Custom" },
                ...IC_LORA_PRESETS.map((preset) => ({
                    value: preset.id,
                    label: preset.displayName,
                })),
            ],
            entry.preset,
            (value) => {
                ctx.commit((clips) => {
                    const target = entryField(clips, entryIdx);
                    if (!target) {
                        return;
                    }
                    target.preset = value;
                    const preset = findIcLoraPreset(value);
                    if (preset) {
                        target.strength = preset.strength;
                        target.controlType = preset.controlType;
                    }
                });
                // A rejected [AUTO] download retries when its preset is re-picked.
                clearIcLoraAutoFailure(value);
                ctx.render();
            },
        );
        fields.appendChild(buildField("Preset", presetSelect));

        const loraSelect = buildSelect(
            [IC_LORA_AUTO, ...defaults.loraValues],
            [IC_LORA_AUTO, ...defaults.loraLabels],
            entry.lora,
            (value) => {
                ctx.commit((clips) => {
                    const target = entryField(clips, entryIdx);
                    if (target) {
                        target.lora = value;
                    }
                });
                if (value === IC_LORA_AUTO) {
                    clearIcLoraAutoFailure(entry.preset);
                }
                ctx.render();
            },
        );
        fields.appendChild(buildField("LoRA", loraSelect));

        const strength = ctx.buildClampedNumber({
            key: `iclora-${entryIdx}-strength`,
            value: entry.strength,
            min: IC_LORA_STRENGTH_MIN,
            max: IC_LORA_STRENGTH_MAX,
            step: IC_LORA_STRENGTH_STEP,
            readBack: (cs) => entryField(cs, entryIdx)?.strength ?? null,
            mutate: (cs, value) => {
                const target = entryField(cs, entryIdx);
                if (target) {
                    target.strength = value;
                }
            },
        });
        fields.appendChild(buildField("Strength", strength));

        const attention = ctx.buildClampedNumber({
            key: `iclora-${entryIdx}-attention`,
            value: entry.attentionStrength,
            min: IC_LORA_ATTENTION_MIN,
            max: IC_LORA_ATTENTION_MAX,
            step: IC_LORA_ATTENTION_STEP,
            readBack: (cs) =>
                entryField(cs, entryIdx)?.attentionStrength ?? null,
            mutate: (cs, value) => {
                const target = entryField(cs, entryIdx);
                if (target) {
                    target.attentionStrength = value;
                }
            },
        });
        fields.appendChild(buildField("Attention", attention));

        /**
         * Control renders the drive video into a signal; only Union Control
         * (or a custom control LoRA) consumes one, so other curated presets
         * hide the select (their preset pick already reset it to "none").
         */
        const preset = findIcLoraPreset(entry.preset);
        if (!preset || preset.id === IC_LORA_PRESET_UNION_CONTROL_ID) {
            const controlSelect = buildOptionSelect(
                [
                    { value: "none", label: "None (raw video)" },
                    { value: "canny", label: "Canny edges" },
                    { value: "depth", label: "Depth map" },
                    { value: "normal", label: "Normal map" },
                ],
                entry.controlType,
                (value) => {
                    ctx.commit((clips) => {
                        const target = entryField(clips, entryIdx);
                        if (target) {
                            target.controlType = value as IcLoraControlType;
                        }
                    });
                },
            );
            fields.appendChild(buildField("Control", controlSelect));
        }

        const applySelect = buildOptionSelect(
            [
                { value: `${IC_LORA_STAGE_ALL}`, label: "All stages" },
                ...clip.stages.map((_, stageIdx) => ({
                    value: `${stageIdx}`,
                    label: `Stage ${stageIdx}`,
                })),
            ],
            `${entry.stage}`,
            (value) => {
                ctx.commit((clips) => {
                    const target = entryField(clips, entryIdx);
                    if (!target) {
                        return;
                    }
                    const stage = Number(value);
                    target.stage =
                        Number.isInteger(stage) && stage >= 0
                            ? stage
                            : IC_LORA_STAGE_ALL;
                    reconcileIcLoraStage(target);
                });
                ctx.render();
            },
        );
        fields.appendChild(buildField("Apply on", applySelect));

        /**
         * "Stage input" (the previous stage's output as drive media) only
         * exists on refine-stage placements; legacy captured-branch entries
         * keep their source and get no select.
         */
        if (
            entry.stage >= 1 &&
            (entry.source === IC_LORA_SOURCE_UPLOAD ||
                entry.source === IC_LORA_SOURCE_STAGE_INPUT)
        ) {
            const sourceSelect = buildOptionSelect(
                [
                    { value: IC_LORA_SOURCE_UPLOAD, label: "Upload" },
                    {
                        value: IC_LORA_SOURCE_STAGE_INPUT,
                        label: "Stage input",
                    },
                ],
                entry.source,
                (value) => {
                    ctx.commit((clips) => {
                        const target = entryField(clips, entryIdx);
                        if (target) {
                            target.source = value;
                        }
                    });
                    ctx.render();
                },
            );
            fields.appendChild(buildField("Source", sourceSelect));
        }

        if (entry.source === IC_LORA_SOURCE_STAGE_INPUT) {
            const hint = document.createElement("small");
            hint.className = "vst-audio-field-hint";
            hint.textContent = `Driven by stage ${entry.stage}'s input (the previous stage's output).`;
            fields.appendChild(hint);
        } else if (entry.source === IC_LORA_SOURCE_UPLOAD) {
            fields.appendChild(
                buildUploadRow(
                    "Drive Media",
                    "video/*,image/*",
                    entry.video?.fileName,
                    (data, fileName) => {
                        ctx.commit((clips) => {
                            const target = entryField(clips, entryIdx);
                            if (target) {
                                target.video = { data, fileName };
                            }
                        });
                        ctx.render();
                    },
                    () => {
                        ctx.commit((clips) => {
                            const target = entryField(clips, entryIdx);
                            if (target) {
                                target.video = null;
                            }
                        });
                        ctx.render();
                    },
                ),
            );
            if (entry.video?.data?.startsWith("data:video/")) {
                const voiceRow = buildCheckbox(
                    "Voice ref from drive audio",
                    entry.driveAudioRef === true,
                    (value) => {
                        ctx.commit((clips) => {
                            const target = entryField(clips, entryIdx);
                            if (target) {
                                target.driveAudioRef = value;
                            }
                        });
                    },
                );
                voiceRow.title =
                    "Use this video's audio as the speaker sample " +
                    "(LipDub): new speech matching the prompt is " +
                    "generated in that voice.";
                fields.appendChild(voiceRow);
            }
        } else {
            const slot = document.createElement("small");
            slot.className = "vst-audio-field-hint";
            slot.textContent = `Driven by ${entry.source} (legacy source)`;
            fields.appendChild(slot);
        }

        const hintText = [preset?.note ?? "", icLoraTriggerHint(preset)]
            .filter(Boolean)
            .join(" ");
        if (hintText || preset) {
            const hint = document.createElement("small");
            hint.className = "vst-audio-field-hint";
            hint.textContent = hintText ? `${hintText} ` : "";
            if (preset) {
                const link = document.createElement("a");
                link.href = icLoraRepoUrl(preset);
                link.target = "_blank";
                link.rel = "noopener";
                link.textContent = "repo";
                hint.appendChild(link);
            }
            fields.appendChild(hint);
        }

        /**
         * [AUTO] fulfillment: start the preset-weights download when needed and surface its
         * state on a tagged line the downloader repaints in place as progress streams in.
         */
        ensureIcLoraAutoWeights(entry, defaults.loraValues, ctx.render);
        const autoText = icLoraAutoHint(entry, defaults.loraValues);
        if (autoText) {
            const autoHint = document.createElement("small");
            autoHint.className = "vst-audio-field-hint";
            if (preset) {
                autoHint.setAttribute(IC_LORA_AUTO_HINT_ATTR, preset.id);
            }
            autoHint.textContent = autoText;
            fields.appendChild(autoHint);
        }

        col.appendChild(row);
    });

    col.appendChild(
        buildAddButton("+ Add IC-LoRA", "vst-detail-add-iclora", () => {
            ctx.structuralCommit((clips) => {
                const target = clips[clipIdx];
                if (!target) {
                    return null;
                }
                target.icLoras.push(
                    defaultIcLora({
                        lora: defaults.loraValues[0] ?? IC_LORA_AUTO,
                    }),
                );
                return "render";
            });
        }),
    );
    return wrap;
};

export const buildClipBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "clip" }>,
    clips: Clip[],
): HTMLElement => {
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-clip-body";
    const clip = clips[sel.clipIdx];
    const stage = clip.stages[sel.stageIdx];
    const defaults = getRootDefaults();
    /**
     * The clip's own fields render bare (no group header/container — the
     * dock breadcrumb already names the clip), followed by a Stages group
     * (labelled rail + the selected stage's parameters) and a Retake group
     * — retake editing lives in the clip panel, like stages.
     */
    body.appendChild(buildClipColumn(ctx, clip, sel.clipIdx));
    const params = buildParamsColumn(
        ctx,
        clip,
        sel.clipIdx,
        sel.stageIdx,
        stage,
        defaults,
    );
    const stagesWrap = document.createElement("div");
    stagesWrap.className = "vst-detail-stages-wrap";
    stagesWrap.append(
        sectionLabel("Stages"),
        buildStageRail(ctx, clip, sel.clipIdx, sel.stageIdx),
        params.col,
    );
    body.appendChild(buildGroup(GROUP_STAGES, stagesWrap));
    body.appendChild(
        buildGroup(
            GROUP_ICLORA,
            buildIcLorasSection(ctx, clip, sel.clipIdx, defaults),
        ),
    );
    body.appendChild(
        buildGroup(GROUP_RETAKE, buildRetakeSection(ctx, clip, sel.clipIdx)),
    );
    return body;
};
