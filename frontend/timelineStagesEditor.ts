import {
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
import { refSourceLabel } from "./timelineDetail";
import type { RootDefaults, Stage, StageLora } from "./types";

const STAGE_SELECTOR = "[data-vst-stage]";
const STAGE_ADD_SELECTOR = "[data-vst-stage-add]";
const MODEL_SELECTOR = "[data-vst-model]";
const INTERACTIVE_SELECTOR = `${STAGE_SELECTOR}, ${STAGE_ADD_SELECTOR}, ${MODEL_SELECTOR}`;
const EDITING_CLASS = "vst-stage-editing";
const LORA_WEIGHT_STEP = 0.05;
const LORA_WEIGHT_DEFAULT = 1;

let sliderSeq = 0;

export interface TimelineStagesEditor {
    attach(body: HTMLElement): void;
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

    const findStageChip = (
        clipIdx: number,
        stageIdx: number,
    ): HTMLElement | null =>
        boundBody?.querySelector<HTMLElement>(
            `${STAGE_SELECTOR}[data-clip-idx="${clipIdx}"][data-stage-idx="${stageIdx}"]`,
        ) ?? null;

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
        const width = clamp(Math.round(hostRect.width - 32), 260, 420);
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

    const openModelEditor = (anchor: HTMLElement, clipIdx: number): void => {
        const clip = getClips()[clipIdx];
        const stage0 = clip?.stages?.[0];
        if (!clip || !stage0) {
            return;
        }
        const sourceJson = readStateToken();
        const defaults = getRootDefaults();
        const wrap = mountInspector(anchor, "vst-stage-model-inspector");

        const head = document.createElement("div");
        head.className = "vst-prompt-inspector-head";
        head.textContent = `Clip ${clipIdx} · model`;

        let selected = `${stage0.model ?? ""}`;
        const select = buildSelect(
            defaults.modelValues,
            defaults.modelLabels,
            selected,
            (value) => {
                selected = value;
                finish(true);
            },
        );
        const field = buildField("Model", select, "(applies to Stage 0)");

        const hint = document.createElement("div");
        hint.className = "vst-prompt-inspector-hint";
        hint.textContent = "Pick a model to apply · Esc to cancel";

        wrap.append(head, field, hint);

        let done = false;
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
            const target = clips[clipIdx]?.stages?.[0];
            if (!target || target.model === selected) {
                return;
            }
            target.model = selected;
            saveClips(clips);
        };

        wireDismiss(wrap, ".vst-stage-model-inspector", finish);
        document.body.appendChild(wrap);
        clampInspectorToViewport(wrap);
        activeWrap = wrap;
        select.focus();
    };

    const buildLoraSection = (
        draft: StageDraft,
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

    const openStageEditor = (
        anchor: HTMLElement | null,
        clipIdx: number,
        stageIdx: number,
    ): void => {
        const clip = getClips()[clipIdx];
        const stage = clip?.stages?.[stageIdx];
        if (!clip || !stage) {
            return;
        }
        const sourceJson = readStateToken();
        const defaults = getRootDefaults();
        const draft = draftFromStage(stage);
        const isRefine = stageIdx >= 1;
        const canDelete = clip.stages.length > 1;

        const wrap = mountInspector(anchor, "vst-stage-inspector");

        const head = document.createElement("div");
        head.className = "vst-prompt-inspector-head";
        head.textContent = `Stage ${stageIdx} · ${isRefine ? "refine" : "full gen"}`;
        wrap.appendChild(head);

        wrap.appendChild(
            buildCheckbox("Skip this stage", draft.skipped, (value) => {
                draft.skipped = value;
            }),
        );

        wrap.appendChild(
            buildField(
                "Model",
                buildSelect(
                    defaults.modelValues,
                    defaults.modelLabels,
                    draft.model,
                    (value) => {
                        draft.model = value;
                    },
                ),
            ),
        );

        wrap.appendChild(
            buildSlider(
                "Steps",
                draft.steps,
                defaults.stepsMin,
                defaults.stepsMax,
                defaults.stepsStep,
                (value) => {
                    draft.steps = Math.round(value);
                },
            ),
        );
        wrap.appendChild(
            buildSlider(
                "CFG Scale",
                draft.cfgScale,
                defaults.cfgScaleMin,
                defaults.cfgScaleMax,
                defaults.cfgScaleStep,
                (value) => {
                    draft.cfgScale = value;
                },
            ),
        );

        if (isRefine) {
            wrap.appendChild(
                buildSlider(
                    "Control (regen strength)",
                    draft.control,
                    defaults.controlMin,
                    defaults.controlMax,
                    defaults.controlStep,
                    (value) => {
                        draft.control = value;
                    },
                ),
            );
            wrap.appendChild(
                buildSlider(
                    "Upscale",
                    draft.upscale,
                    defaults.upscaleMin,
                    defaults.upscaleMax,
                    defaults.upscaleStep,
                    (value) => {
                        draft.upscale = value;
                    },
                ),
            );
            wrap.appendChild(
                buildField(
                    "Upscale Method",
                    buildSelect(
                        defaults.upscaleMethodValues,
                        defaults.upscaleMethodLabels,
                        draft.upscaleMethod,
                        (value) => {
                            draft.upscaleMethod = value;
                        },
                    ),
                ),
            );
        }

        wrap.appendChild(
            buildField(
                "Sampler",
                buildSelect(
                    defaults.samplerValues,
                    defaults.samplerLabels,
                    draft.sampler,
                    (value) => {
                        draft.sampler = value;
                    },
                ),
            ),
        );
        wrap.appendChild(
            buildField(
                "Scheduler",
                buildSelect(
                    defaults.schedulerValues,
                    defaults.schedulerLabels,
                    draft.scheduler,
                    (value) => {
                        draft.scheduler = value;
                    },
                ),
            ),
        );

        wrap.appendChild(buildLoraSection(draft, defaults));

        if (clip.refs.length > 0) {
            const refsSection = document.createElement("div");
            refsSection.className = "vst-audio-field vst-stage-refs";
            const refsLabel = document.createElement("span");
            refsLabel.className = "vst-audio-field-label";
            refsLabel.textContent = "Reference Strengths";
            refsSection.appendChild(refsLabel);
            clip.refs.forEach((ref, refIdx) => {
                if (refIdx >= draft.refStrengths.length) {
                    draft.refStrengths[refIdx] = STAGE_REF_STRENGTH_MAX;
                }
                const slider = buildSlider(
                    `R${refIdx}`,
                    draft.refStrengths[refIdx],
                    STAGE_REF_STRENGTH_MIN,
                    STAGE_REF_STRENGTH_MAX,
                    STAGE_REF_STRENGTH_STEP,
                    (value) => {
                        draft.refStrengths[refIdx] = value;
                    },
                    {
                        title: `${refSourceLabel(ref.source ?? "")} · frame ${ref.frame ?? 0}${ref.fromEnd ? " (from end)" : ""}`,
                    },
                );
                slider.classList.add("vst-stage-ref-slider");
                refsSection.appendChild(slider);
            });
            wrap.appendChild(refsSection);
        }

        wrap.appendChild(
            buildSlider(
                "ControlNet Strength",
                draft.controlNetStrength,
                STAGE_CONTROLNET_STRENGTH_MIN,
                STAGE_CONTROLNET_STRENGTH_MAX,
                STAGE_CONTROLNET_STRENGTH_STEP,
                (value) => {
                    draft.controlNetStrength = value;
                },
                { hint: "Only applies when a ControlNet source is set" },
            ),
        );

        let done = false;
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
            const target = clips[clipIdx]?.stages?.[stageIdx];
            if (!target) {
                return;
            }
            target.model = draft.model;
            target.steps = draft.steps;
            target.cfgScale = draft.cfgScale;
            target.sampler = draft.sampler;
            target.scheduler = draft.scheduler;
            target.skipped = draft.skipped;
            target.controlNetStrength = draft.controlNetStrength;
            target.refStrengths = draft.refStrengths.slice(
                0,
                clips[clipIdx].refs.length,
            );
            target.loras = draft.loras
                .filter((lora) => `${lora.name ?? ""}`.trim() !== "")
                .map((lora) => ({
                    name: lora.name,
                    weight: Number.isFinite(lora.weight) ? lora.weight : 1,
                }));
            if (isRefine) {
                target.control = draft.control;
                target.upscale = draft.upscale;
                target.upscaleMethod = draft.upscaleMethod;
            }
            saveClips(clips);
        };

        if (canDelete) {
            const deleteBtn = document.createElement("button");
            deleteBtn.type = "button";
            deleteBtn.className = "vst-refs-delete";
            deleteBtn.textContent = "Delete stage";
            deleteBtn.addEventListener("click", (event) => {
                event.preventDefault();
                if (done) {
                    return;
                }
                done = true;
                closeEditor();
                deleteStage(clipIdx, stageIdx, sourceJson);
            });
            wrap.appendChild(deleteBtn);
        }

        const hint = document.createElement("div");
        hint.className = "vst-prompt-inspector-hint";
        hint.textContent = "Click away to apply · Esc to cancel";
        wrap.appendChild(hint);

        wireDismiss(wrap, ".vst-stage-inspector", finish);
        document.body.appendChild(wrap);
        enableSlidersIn(wrap);
        clampInspectorToViewport(wrap);
        activeWrap = wrap;
        const firstSelect = wrap.querySelector<HTMLSelectElement>("select");
        firstSelect?.focus();
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
        openStageEditor(findStageChip(clipIdx, newIdx), clipIdx, newIdx);
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
                openStageEditor(stageChip, clipIdx, stageIdx);
            }
            return;
        }
        const modelBadge = target.closest(MODEL_SELECTOR);
        if (modelBadge instanceof HTMLElement) {
            const clipIdx = parseIntAttr(modelBadge, "data-clip-idx");
            if (clipIdx !== null) {
                openModelEditor(modelBadge, clipIdx);
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

    return { attach, dispose };
};
