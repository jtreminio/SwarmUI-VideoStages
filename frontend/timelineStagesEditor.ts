import {
    CLIP_DURATION_MAX,
    CLIP_DURATION_MIN,
    clamp,
    STAGE_CONTROLNET_STRENGTH_MAX,
    STAGE_CONTROLNET_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_STEP,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_STEP,
} from "./constants";
import { buildDefaultStage } from "./normalization";
import { getClips, saveClips } from "./persistence";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import { readStateToken } from "./swarmInputs";
import {
    refSourceLabel,
    stageChipLabel,
    stageChipTitle,
} from "./timelineDetail";
import { applyClipDurationResize } from "./timelineEdit";
import type { RefImage, RootDefaults, Stage, StageLora } from "./types";

const STAGE_SELECTOR = "[data-vst-stage]";
const STAGE_ADD_SELECTOR = "[data-vst-stage-add]";
const MODEL_SELECTOR = "[data-vst-model]";
const INTERACTIVE_SELECTOR = `${STAGE_SELECTOR}, ${STAGE_ADD_SELECTOR}, ${MODEL_SELECTOR}`;
const EDITING_CLASS = "vst-stage-editing";
const LORA_WEIGHT_STEP = 0.05;
const LORA_WEIGHT_DEFAULT = 1;
const DURATION_STEP = 0.1;
const UPSCALE_EPSILON = 1e-6;

let sliderSeq = 0;

export interface ClipEditorOptions {
    stageIdx?: number;
    focusModel?: boolean;
}

export interface TimelineStagesEditor {
    attach(body: HTMLElement): void;
    openClipEditor(
        anchor: HTMLElement | null,
        clipIdx: number,
        opts?: ClipEditorOptions,
    ): void;
    dispose(): void;
}

interface StageDraft {
    model: string;
    steps: number;
    cfgScale: number;
    control: number;
    upscale: number;
    upscaleMethod: string;
    sampler: string;
    scheduler: string;
    skipped: boolean;
    controlNetStrength: number;
    refStrengths: number[];
    loras: StageLora[];
}

interface ClipStageDraft extends StageDraft {
    originalIdx: number | null;
}

interface ClipDraft {
    clipSkipped: boolean;
    duration: number;
    stages: ClipStageDraft[];
}

const parseIntAttr = (el: Element | null, name: string): number | null => {
    if (!el) {
        return null;
    }
    const raw = el.getAttribute(name);
    if (raw === null) {
        return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
};

const draftFromStage = (stage: Stage): StageDraft => ({
    model: `${stage.model ?? ""}`,
    steps: stage.steps,
    cfgScale: stage.cfgScale,
    control: stage.control,
    upscale: stage.upscale,
    upscaleMethod: `${stage.upscaleMethod ?? ""}`,
    sampler: `${stage.sampler ?? ""}`,
    scheduler: `${stage.scheduler ?? ""}`,
    skipped: stage.skipped === true,
    controlNetStrength: stage.controlNetStrength,
    refStrengths: (stage.refStrengths ?? []).slice(),
    loras: (stage.loras ?? []).map((lora) => ({
        name: lora.name,
        weight: lora.weight,
    })),
});

const stageFromDraft = (draft: ClipStageDraft): Stage => ({
    expanded: true,
    skipped: draft.skipped,
    control: draft.control,
    controlNetStrength: draft.controlNetStrength,
    refStrengths: draft.refStrengths.slice(),
    upscale: draft.upscale,
    upscaleMethod: draft.upscaleMethod,
    model: draft.model,
    steps: draft.steps,
    cfgScale: draft.cfgScale,
    sampler: draft.sampler,
    scheduler: draft.scheduler,
    loras: draft.loras.map((lora) => ({
        name: lora.name,
        weight: lora.weight,
    })),
});

export const createTimelineStagesEditor = (): TimelineStagesEditor => {
    let boundBody: HTMLElement | null = null;
    let activeWrap: HTMLElement | null = null;
    let editingAnchor: HTMLElement | null = null;
    let outsideMouseHandler: ((event: MouseEvent) => void) | null = null;

    const isStale = (sourceJson: string): boolean =>
        readStateToken() !== sourceJson;

    const closeEditor = (): void => {
        if (outsideMouseHandler) {
            document.removeEventListener(
                "mousedown",
                outsideMouseHandler,
                true,
            );
            outsideMouseHandler = null;
        }
        if (editingAnchor) {
            editingAnchor.classList.remove(EDITING_CLASS);
            editingAnchor = null;
        }
        if (activeWrap) {
            activeWrap.remove();
            activeWrap = null;
        }
    };

    const buildField = (
        label: string,
        control: HTMLElement,
        hint?: string,
    ): HTMLElement => {
        const row = document.createElement("div");
        row.className = "vst-audio-field";
        const text = document.createElement("span");
        text.className = "vst-audio-field-label";
        text.textContent = label;
        row.append(text, control);
        if (hint) {
            const small = document.createElement("small");
            small.className = "vst-audio-field-hint";
            small.textContent = hint;
            row.appendChild(small);
        }
        return row;
    };

    const buildSelect = (
        values: string[],
        labels: string[],
        selected: string,
        onChange: (value: string) => void,
    ): HTMLSelectElement => {
        const select = document.createElement("select");
        select.className = "vst-audio-select";
        for (let i = 0; i < values.length; i++) {
            const opt = document.createElement("option");
            opt.value = values[i];
            opt.textContent = labels[i] ?? values[i];
            opt.dataset.cleanname = labels[i] ?? values[i];
            opt.selected = values[i] === selected;
            select.appendChild(opt);
        }
        select.addEventListener("change", () => onChange(select.value));
        return select;
    };

    const buildNumber = (
        value: number,
        min: number,
        max: number,
        step: number,
        onChange: (value: number) => void,
    ): HTMLInputElement => {
        const input = document.createElement("input");
        input.type = "number";
        input.className = "vst-refs-num";
        input.min = `${min}`;
        input.max = `${max}`;
        input.step = `${step}`;
        input.value = `${value}`;
        const apply = (normalize: boolean): void => {
            const parsed = Number.parseFloat(input.value);
            const next = clamp(
                Number.isFinite(parsed) ? parsed : value,
                min,
                max,
            );
            onChange(next);
            if (normalize) {
                input.value = `${next}`;
            }
        };
        input.addEventListener("input", () => apply(false));
        input.addEventListener("change", () => apply(true));
        return input;
    };

    const buildSlider = (
        label: string,
        value: number,
        min: number,
        max: number,
        step: number,
        onChange: (value: number) => void,
        opts?: { hint?: string; title?: string },
    ): HTMLElement => {
        const holder = document.createElement("div");
        holder.className = "vst-stage-slider";
        const id = `vst_stage_slider_${++sliderSeq}`;
        holder.innerHTML = makeSliderInput(
            null,
            id,
            "",
            label,
            "",
            value,
            min,
            max,
            min,
            max,
            step,
            false,
            false,
            false,
        );
        const number = holder.querySelector<HTMLInputElement>(
            "input.auto-slider-number",
        );
        if (number) {
            const apply = (normalize: boolean): void => {
                const parsed = Number.parseFloat(number.value);
                const next = clamp(
                    Number.isFinite(parsed) ? parsed : value,
                    min,
                    max,
                );
                onChange(next);
                if (normalize) {
                    number.value = `${next}`;
                }
            };
            number.addEventListener("input", () => apply(false));
            number.addEventListener("change", () => apply(true));
        }
        if (opts?.title) {
            holder.title = opts.title;
        }
        if (opts?.hint) {
            const small = document.createElement("small");
            small.className = "vst-audio-field-hint";
            small.textContent = opts.hint;
            holder.appendChild(small);
        }
        return holder;
    };

    const buildCheckbox = (
        label: string,
        checked: boolean,
        onChange: (value: boolean) => void,
    ): HTMLElement => {
        const row = document.createElement("label");
        row.className = "vst-audio-field vst-audio-field-check";
        const input = document.createElement("input");
        input.type = "checkbox";
        input.checked = checked;
        input.addEventListener("change", () => onChange(input.checked));
        const text = document.createElement("span");
        text.className = "vst-audio-field-label";
        text.textContent = label;
        row.append(input, text);
        return row;
    };

    const mountInspector = (
        anchor: HTMLElement | null,
        extraClass: string,
    ): HTMLElement => {
        closeEditor();
        const host = boundBody ?? document.body;
        const hostRect = host.getBoundingClientRect();
        const viewportW =
            window.innerWidth || document.documentElement.clientWidth;
        const isClip = extraClass.split(/\s+/).includes("vst-clip-inspector");
        const width = isClip
            ? clamp(Math.round(hostRect.width - 32), 480, 680)
            : clamp(Math.round(hostRect.width - 32), 260, 420);
        const left = clamp(
            Math.round(hostRect.left + (hostRect.width - width) / 2),
            8,
            Math.max(8, viewportW - width - 8),
        );
        const wrap = document.createElement("div");
        wrap.className = `vst-prompt-inspector ${extraClass}`;
        wrap.style.left = `${left}px`;
        wrap.style.top = `${Math.round(Math.max(8, hostRect.top + 46))}px`;
        wrap.style.width = `${width}px`;
        if (anchor) {
            anchor.classList.add(EDITING_CLASS);
            editingAnchor = anchor;
        }
        return wrap;
    };

    const clampInspectorToViewport = (wrap: HTMLElement): void => {
        const viewportH =
            window.innerHeight || document.documentElement.clientHeight;
        wrap.style.maxHeight = `${Math.min(Math.round(viewportH * 0.78), viewportH - 16)}px`;
        const height = wrap.offsetHeight;
        const currentTop = Number.parseFloat(wrap.style.top) || 0;
        const newTop = clamp(
            currentTop,
            8,
            Math.max(8, viewportH - height - 8),
        );
        wrap.style.top = `${newTop}px`;
        if (newTop + height > viewportH - 8) {
            wrap.style.maxHeight = `${Math.max(64, viewportH - newTop - 8)}px`;
        }
    };

    const wireDismiss = (
        wrap: HTMLElement,
        inspectorSelector: string,
        finish: (save: boolean) => void,
    ): void => {
        wrap.addEventListener("keydown", (event) => {
            if (
                event.target instanceof Element &&
                event.target.closest(".sui-popover")
            ) {
                return;
            }
            if (event.key === "Escape") {
                event.preventDefault();
                finish(false);
            } else if (
                event.key === "Enter" &&
                !(event.target instanceof HTMLSelectElement)
            ) {
                event.preventDefault();
                finish(true);
            }
            event.stopPropagation();
        });
        const onOutside = (event: MouseEvent): void => {
            const target = event.target;
            if (!(target instanceof Element)) {
                return;
            }
            if (
                target.closest(inspectorSelector) ||
                target.closest(".sui-popover")
            ) {
                return;
            }
            finish(true);
        };
        outsideMouseHandler = onOutside;
        document.addEventListener("mousedown", onOutside, true);
    };

    const buildLoraSection = (
        draft: ClipStageDraft,
        defaults: RootDefaults,
    ): HTMLElement => {
        const section = document.createElement("div");
        section.className = "vst-audio-field vst-stage-loras";
        const label = document.createElement("span");
        label.className = "vst-audio-field-label";
        label.textContent = "LoRAs";
        section.appendChild(label);

        if (defaults.loraValues.length === 0) {
            const empty = document.createElement("small");
            empty.className = "vst-audio-field-hint";
            empty.textContent = "(no LoRAs available)";
            section.appendChild(empty);
            return section;
        }

        const list = document.createElement("div");
        list.className = "vst-stage-lora-list";
        section.appendChild(list);

        const addBtn = document.createElement("button");
        addBtn.type = "button";
        addBtn.className = "vst-stage-lora-add";
        addBtn.textContent = "+ Add LoRA";
        section.appendChild(addBtn);

        const renderRows = (): void => {
            list.innerHTML = "";
            draft.loras.forEach((lora, index) => {
                const row = document.createElement("div");
                row.className = "vst-stage-lora-row";
                const select = buildSelect(
                    defaults.loraValues,
                    defaults.loraLabels,
                    lora.name,
                    (value) => {
                        draft.loras[index].name = value;
                    },
                );
                const weight = buildNumber(
                    lora.weight,
                    -10,
                    10,
                    LORA_WEIGHT_STEP,
                    (value) => {
                        draft.loras[index].weight = value;
                    },
                );
                weight.classList.add("vst-stage-lora-weight");
                const remove = document.createElement("button");
                remove.type = "button";
                remove.className = "vst-stage-lora-remove";
                remove.textContent = "×";
                remove.title = "Remove this LoRA";
                remove.addEventListener("click", () => {
                    draft.loras.splice(index, 1);
                    renderRows();
                });
                row.append(select, weight, remove);
                list.appendChild(row);
            });
        };

        addBtn.addEventListener("click", () => {
            draft.loras.push({
                name: defaults.loraValues[0] ?? "",
                weight: LORA_WEIGHT_DEFAULT,
            });
            renderRows();
        });
        renderRows();
        return section;
    };

    const openClipEditor = (
        anchor: HTMLElement | null,
        clipIdx: number,
        opts?: ClipEditorOptions,
    ): void => {
        const clip = getClips()[clipIdx];
        if (!clip?.stages || clip.stages.length === 0) {
            return;
        }
        const sourceJson = readStateToken();
        const defaults = getRootDefaults();
        const refs: RefImage[] = clip.refs.slice();
        const lengthDerived =
            clip.clipLengthFromAudio === true ||
            clip.clipLengthFromControlNet === true;

        const draft: ClipDraft = {
            clipSkipped: clip.skipped === true,
            duration: clip.duration,
            stages: clip.stages.map((stage, idx) => ({
                originalIdx: idx,
                ...draftFromStage(stage),
            })),
        };

        let activeStageIdx = clamp(
            opts?.stageIdx ?? 0,
            0,
            draft.stages.length - 1,
        );
        const focusModel = opts?.focusModel === true;

        const wrap = mountInspector(anchor, "vst-clip-inspector");

        const head = document.createElement("div");
        head.className = "vst-prompt-inspector-head";
        const syncHead = (): void => {
            head.textContent = `Clip ${clipIdx} · ${draft.duration}s`;
        };
        syncHead();
        wrap.appendChild(head);

        const clipSection = document.createElement("div");
        clipSection.className = "vst-clip-section";
        clipSection.appendChild(
            buildCheckbox("Skip this clip", draft.clipSkipped, (value) => {
                draft.clipSkipped = value;
            }),
        );
        const durationInput = buildNumber(
            draft.duration,
            CLIP_DURATION_MIN,
            CLIP_DURATION_MAX,
            DURATION_STEP,
            (value) => {
                draft.duration = value;
                syncHead();
            },
        );
        const durationField = buildField(
            "Duration (s)",
            durationInput,
            lengthDerived
                ? "(derived from audio/ControlNet source)"
                : undefined,
        );
        if (lengthDerived) {
            durationInput.disabled = true;
            durationField.classList.add("vst-field-disabled");
        }
        clipSection.appendChild(durationField);
        wrap.appendChild(clipSection);

        const divider = document.createElement("div");
        divider.className = "vst-clip-divider";
        wrap.appendChild(divider);

        const tabs = document.createElement("div");
        tabs.className = "vst-stage-tabs";
        wrap.appendChild(tabs);

        const panelHost = document.createElement("div");
        panelHost.className = "vst-stage-panel";
        wrap.appendChild(panelHost);

        const hint = document.createElement("div");
        hint.className = "vst-prompt-inspector-hint";
        hint.textContent = "Click away to apply · Esc to cancel";
        wrap.appendChild(hint);

        let done = false;

        const setActive = (idx: number): void => {
            activeStageIdx = clamp(idx, 0, draft.stages.length - 1);
            renderTabs();
            renderPanel();
        };

        const addDraftStage = (): void => {
            const last = draft.stages[draft.stages.length - 1] ?? null;
            const previous = last ? stageFromDraft(last) : null;
            const created = buildDefaultStage(
                getRootDefaults,
                getDefaultStageModel,
                previous,
                refs.length,
            );
            draft.stages.push({
                originalIdx: null,
                ...draftFromStage(created),
            });
            setActive(draft.stages.length - 1);
        };

        const deleteDraftStage = (idx: number): void => {
            if (draft.stages.length <= 1) {
                return;
            }
            draft.stages.splice(idx, 1);
            setActive(Math.max(0, idx - 1));
        };

        const renderTabs = (): void => {
            tabs.innerHTML = "";
            draft.stages.forEach((sd, idx) => {
                const tab = document.createElement("button");
                tab.type = "button";
                tab.className = "vst-chip vst-stage-tab";
                if (idx === activeStageIdx) {
                    tab.classList.add("vst-stage-tab-active");
                }
                if (sd.skipped) {
                    tab.classList.add("vst-stage-tab-skipped");
                }
                tab.textContent = stageChipLabel(idx);
                tab.title = stageChipTitle(stageFromDraft(sd), idx);
                tab.addEventListener("click", () => setActive(idx));
                tabs.appendChild(tab);
            });
            const addTab = document.createElement("button");
            addTab.type = "button";
            addTab.className = "vst-chip vst-stage-tab vst-stage-tab-add";
            addTab.textContent = "+";
            addTab.title = "Add a refine stage";
            addTab.addEventListener("click", () => addDraftStage());
            tabs.appendChild(addTab);
        };

        const buildStagePanel = (
            sd: ClipStageDraft,
            draftIdx: number,
        ): HTMLElement => {
            const panel = document.createElement("div");
            panel.className = "vst-stage-panel-inner";
            const isRefine = draftIdx >= 1;

            const fieldsWrap = document.createElement("div");
            fieldsWrap.className = "vst-stage-fields";
            const applyMute = (): void => {
                fieldsWrap.classList.toggle(
                    "vst-stage-fields-muted",
                    sd.skipped,
                );
            };
            panel.appendChild(
                buildCheckbox("Skip this stage", sd.skipped, (value) => {
                    sd.skipped = value;
                    applyMute();
                    renderTabs();
                }),
            );
            panel.appendChild(fieldsWrap);
            applyMute();

            const modelField = buildField(
                "Model",
                buildSelect(
                    defaults.modelValues,
                    defaults.modelLabels,
                    sd.model,
                    (value) => {
                        sd.model = value;
                    },
                ),
            );
            modelField.classList.add("vst-span-2");
            fieldsWrap.appendChild(modelField);

            fieldsWrap.appendChild(
                buildSlider(
                    "Steps",
                    sd.steps,
                    defaults.stepsMin,
                    defaults.stepsMax,
                    defaults.stepsStep,
                    (value) => {
                        sd.steps = Math.round(value);
                    },
                ),
            );
            fieldsWrap.appendChild(
                buildSlider(
                    "CFG Scale",
                    sd.cfgScale,
                    defaults.cfgScaleMin,
                    defaults.cfgScaleMax,
                    defaults.cfgScaleStep,
                    (value) => {
                        sd.cfgScale = value;
                    },
                ),
            );

            if (isRefine) {
                fieldsWrap.appendChild(
                    buildSlider(
                        "Control",
                        sd.control,
                        defaults.controlMin,
                        defaults.controlMax,
                        defaults.controlStep,
                        (value) => {
                            sd.control = value;
                        },
                        {
                            title: "Regen strength — higher = more of the stage is re-generated",
                        },
                    ),
                );
                const methodSelect = buildSelect(
                    defaults.upscaleMethodValues,
                    defaults.upscaleMethodLabels,
                    sd.upscaleMethod,
                    (value) => {
                        sd.upscaleMethod = value;
                    },
                );
                const methodField = buildField("Upscale Method", methodSelect);
                methodField.classList.add("vst-span-2");
                const syncMethod = (): void => {
                    const disabled = Math.abs(sd.upscale - 1) < UPSCALE_EPSILON;
                    methodSelect.disabled = disabled;
                    methodField.classList.toggle(
                        "vst-field-disabled",
                        disabled,
                    );
                    methodField.title = disabled
                        ? "Set Upscale above 1× to choose a method"
                        : "";
                };
                fieldsWrap.appendChild(
                    buildSlider(
                        "Upscale",
                        sd.upscale,
                        defaults.upscaleMin,
                        defaults.upscaleMax,
                        defaults.upscaleStep,
                        (value) => {
                            sd.upscale = value;
                            syncMethod();
                        },
                    ),
                );
                fieldsWrap.appendChild(methodField);
                syncMethod();
            }

            fieldsWrap.appendChild(
                buildField(
                    "Sampler",
                    buildSelect(
                        defaults.samplerValues,
                        defaults.samplerLabels,
                        sd.sampler,
                        (value) => {
                            sd.sampler = value;
                        },
                    ),
                ),
            );
            fieldsWrap.appendChild(
                buildField(
                    "Scheduler",
                    buildSelect(
                        defaults.schedulerValues,
                        defaults.schedulerLabels,
                        sd.scheduler,
                        (value) => {
                            sd.scheduler = value;
                        },
                    ),
                ),
            );

            const loraSection = buildLoraSection(sd, defaults);
            loraSection.classList.add("vst-span-2");
            fieldsWrap.appendChild(loraSection);

            if (refs.length > 0) {
                const refsSection = document.createElement("div");
                refsSection.className =
                    "vst-audio-field vst-stage-refs vst-span-2";
                const refsLabel = document.createElement("span");
                refsLabel.className = "vst-audio-field-label";
                refsLabel.textContent = "Reference Strengths";
                refsSection.appendChild(refsLabel);
                refs.forEach((ref, refIdx) => {
                    if (refIdx >= sd.refStrengths.length) {
                        sd.refStrengths[refIdx] = STAGE_REF_STRENGTH_MAX;
                    }
                    const slider = buildSlider(
                        `R${refIdx}`,
                        sd.refStrengths[refIdx],
                        STAGE_REF_STRENGTH_MIN,
                        STAGE_REF_STRENGTH_MAX,
                        STAGE_REF_STRENGTH_STEP,
                        (value) => {
                            sd.refStrengths[refIdx] = value;
                        },
                        {
                            title: `${refSourceLabel(ref.source ?? "")} · frame ${ref.frame ?? 0}${ref.fromEnd ? " (from end)" : ""}`,
                        },
                    );
                    slider.classList.add("vst-stage-ref-slider");
                    refsSection.appendChild(slider);
                });
                fieldsWrap.appendChild(refsSection);
            }

            const controlNetSlider = buildSlider(
                "ControlNet Strength",
                sd.controlNetStrength,
                STAGE_CONTROLNET_STRENGTH_MIN,
                STAGE_CONTROLNET_STRENGTH_MAX,
                STAGE_CONTROLNET_STRENGTH_STEP,
                (value) => {
                    sd.controlNetStrength = value;
                },
                { hint: "Only applies when a ControlNet source is set" },
            );
            controlNetSlider.classList.add("vst-span-2");
            fieldsWrap.appendChild(controlNetSlider);

            if (draft.stages.length > 1) {
                const deleteBtn = document.createElement("button");
                deleteBtn.type = "button";
                deleteBtn.className = "vst-refs-delete";
                deleteBtn.textContent = "Delete stage";
                deleteBtn.addEventListener("click", (event) => {
                    event.preventDefault();
                    deleteDraftStage(draftIdx);
                });
                panel.appendChild(deleteBtn);
            }

            return panel;
        };

        const renderPanel = (): void => {
            panelHost.innerHTML = "";
            const sd = draft.stages[activeStageIdx];
            if (!sd) {
                return;
            }
            const panel = buildStagePanel(sd, activeStageIdx);
            panelHost.appendChild(panel);
            enableSlidersIn(panel);
            if (wrap.isConnected) {
                clampInspectorToViewport(wrap);
            }
        };

        const finish = (save: boolean): void => {
            if (done) {
                return;
            }
            done = true;
            closeEditor();
            if (!save || isStale(sourceJson)) {
                return;
            }
            const clips = getClips();
            const target = clips[clipIdx];
            if (!target) {
                return;
            }
            target.skipped = draft.clipSkipped;
            if (!lengthDerived && draft.duration !== target.duration) {
                applyClipDurationResize(
                    target,
                    draft.duration,
                    getRootDefaults,
                );
            }
            const originalStages = target.stages.slice();
            const newStages: Stage[] = [];
            draft.stages.forEach((sd, idx) => {
                let stage: Stage;
                if (sd.originalIdx !== null && originalStages[sd.originalIdx]) {
                    stage = originalStages[sd.originalIdx];
                } else {
                    stage = buildDefaultStage(
                        getRootDefaults,
                        getDefaultStageModel,
                        newStages[newStages.length - 1] ?? null,
                        target.refs.length,
                    );
                }
                stage.model = sd.model;
                stage.steps = sd.steps;
                stage.cfgScale = sd.cfgScale;
                stage.sampler = sd.sampler;
                stage.scheduler = sd.scheduler;
                stage.skipped = sd.skipped;
                stage.controlNetStrength = sd.controlNetStrength;
                stage.refStrengths = sd.refStrengths.slice(
                    0,
                    target.refs.length,
                );
                stage.loras = sd.loras
                    .filter((lora) => `${lora.name ?? ""}`.trim() !== "")
                    .map((lora) => ({
                        name: lora.name,
                        weight: Number.isFinite(lora.weight) ? lora.weight : 1,
                    }));
                if (idx >= 1) {
                    stage.control = sd.control;
                    stage.upscale = sd.upscale;
                    stage.upscaleMethod = sd.upscaleMethod;
                }
                newStages.push(stage);
            });
            target.stages = newStages;
            saveClips(clips);
        };

        renderTabs();
        renderPanel();

        wireDismiss(wrap, ".vst-clip-inspector", finish);
        document.body.appendChild(wrap);
        clampInspectorToViewport(wrap);
        activeWrap = wrap;

        if (focusModel) {
            const modelSelect = panelHost.querySelector<HTMLSelectElement>(
                ".vst-audio-field select",
            );
            modelSelect?.focus();
        } else {
            const first = panelHost.querySelector<
                HTMLSelectElement | HTMLInputElement
            >("select, input");
            first?.focus();
        }
    };

    const addStage = (clipIdx: number, sourceJson: string): void => {
        if (isStale(sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip) {
            return;
        }
        const last = clip.stages[clip.stages.length - 1] ?? null;
        clip.stages.push(
            buildDefaultStage(
                getRootDefaults,
                getDefaultStageModel,
                last,
                clip.refs.length,
            ),
        );
        const newIdx = clip.stages.length - 1;
        saveClips(clips);
        const region =
            boundBody?.querySelector<HTMLElement>(
                `.vst-region[data-clip-idx="${clipIdx}"]`,
            ) ?? null;
        openClipEditor(region, clipIdx, { stageIdx: newIdx });
    };

    const deleteStage = (
        clipIdx: number,
        stageIdx: number,
        sourceJson: string,
    ): void => {
        if (isStale(sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip || clip.stages.length <= 1) {
            return;
        }
        if (stageIdx < 0 || stageIdx >= clip.stages.length) {
            return;
        }
        clip.stages.splice(stageIdx, 1);
        saveClips(clips);
    };

    const handleActivation = (target: Element, shiftKey: boolean): void => {
        const addChip = target.closest(STAGE_ADD_SELECTOR);
        if (addChip instanceof HTMLElement) {
            const clipIdx = parseIntAttr(addChip, "data-clip-idx");
            if (clipIdx !== null) {
                addStage(clipIdx, readStateToken());
            }
            return;
        }
        const stageChip = target.closest(STAGE_SELECTOR);
        if (stageChip instanceof HTMLElement) {
            const clipIdx = parseIntAttr(stageChip, "data-clip-idx");
            const stageIdx = parseIntAttr(stageChip, "data-stage-idx");
            if (clipIdx === null || stageIdx === null) {
                return;
            }
            if (shiftKey) {
                deleteStage(clipIdx, stageIdx, readStateToken());
            } else {
                openClipEditor(stageChip, clipIdx, { stageIdx });
            }
            return;
        }
        const modelBadge = target.closest(MODEL_SELECTOR);
        if (modelBadge instanceof HTMLElement) {
            const clipIdx = parseIntAttr(modelBadge, "data-clip-idx");
            if (clipIdx !== null) {
                openClipEditor(modelBadge, clipIdx, {
                    stageIdx: 0,
                    focusModel: true,
                });
            }
        }
    };

    const onMouseDownCapture = (event: MouseEvent): void => {
        if (
            event.target instanceof Element &&
            event.target.closest(INTERACTIVE_SELECTOR)
        ) {
            event.stopPropagation();
        }
    };

    const onClickCapture = (event: MouseEvent): void => {
        if (
            !(event.target instanceof Element) ||
            !event.target.closest(INTERACTIVE_SELECTOR)
        ) {
            return;
        }
        event.stopPropagation();
        handleActivation(event.target, event.shiftKey);
    };

    const onKeyDownCapture = (event: KeyboardEvent): void => {
        if (event.key !== "Enter" && event.key !== " ") {
            return;
        }
        if (
            !(event.target instanceof Element) ||
            !event.target.closest(INTERACTIVE_SELECTOR)
        ) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        handleActivation(event.target, event.shiftKey);
    };

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("mousedown", onMouseDownCapture, true);
        body.addEventListener("click", onClickCapture, true);
        body.addEventListener("keydown", onKeyDownCapture, true);
    };

    const dispose = (): void => {
        closeEditor();
        if (boundBody) {
            boundBody.removeEventListener(
                "mousedown",
                onMouseDownCapture,
                true,
            );
            boundBody.removeEventListener("click", onClickCapture, true);
            boundBody.removeEventListener("keydown", onKeyDownCapture, true);
            boundBody = null;
        }
    };

    return { attach, openClipEditor, dispose };
};
