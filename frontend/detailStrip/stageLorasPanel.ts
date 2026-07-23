import {
    appendHelp,
    buildField,
    buildOptionSelect,
    buildSlider,
    type OptionSpec,
    sectionLabel,
} from "../detailWidgets";
import { preserveSelectedOption } from "../selectOption";
import type { RootDefaults, Stage } from "../types";
import type { DetailStripContext } from "./context";

const LORA_WEIGHT_STEP = 0.05;
const LORA_WEIGHT_DEFAULT = 1;

export const buildStageLorasSection = (
    context: DetailStripContext,
    clipIdx: number,
    stageIdx: number,
    stage: Stage,
    defaults: RootDefaults,
): HTMLElement => {
    const section = document.createElement("div");
    section.className = "vst-stage-loras vst-detail-span-full";
    const heading = sectionLabel("LoRAs");
    section.appendChild(heading);
    appendHelp(
        heading,
        section,
        "Stage LoRAs",
        "LoRAs applied only to this stage. A newly added stage copies every LoRA and weight from the previous stage.",
    );

    const list = document.createElement("div");
    list.className = "vst-stage-lora-list";
    section.appendChild(list);
    stage.loras.forEach((lora, loraIdx) => {
        const entry = document.createElement("div");
        entry.className = "vst-stage-lora-entry";
        const row = document.createElement("div");
        row.className = "vst-stage-lora-row";
        const options: OptionSpec[] = defaults.loraValues.map(
            (value, optionIdx) => ({
                value,
                label: defaults.loraLabels[optionIdx] ?? value,
            }),
        );
        preserveSelectedOption(options, lora.name, "start", (value) => ({
            value,
            label: `${value} (unsupported persisted value)`,
            disabled: true,
        }));
        const select = buildOptionSelect(options, lora.name, (value) => {
            context.commit((clips) => {
                const target = clips[clipIdx]?.stages[stageIdx]?.loras[loraIdx];
                if (target) {
                    target.name = value;
                }
            });
        });
        select.setAttribute(
            "data-vst-focus-key",
            `stage-${stageIdx}-lora-${loraIdx}-model`,
        );
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className =
            "basic-button small-button vst-refs-delete vst-detail-delete vst-stage-lora-remove";
        remove.textContent = "×";
        remove.title = `Delete LoRA ${loraIdx + 1}`;
        remove.setAttribute("aria-label", remove.title);
        remove.addEventListener("click", (event) => {
            event.preventDefault();
            context.structuralCommit(
                (clips) => {
                    const target = clips[clipIdx]?.stages[stageIdx];
                    if (!target?.loras[loraIdx]) {
                        return null;
                    }
                    target.loras.splice(loraIdx, 1);
                    return { kind: "clip", clipIdx, stageIdx };
                },
                { rebuildAfterSelect: true },
            );
        });
        row.append(buildField(`LoRA ${loraIdx + 1}`, select), remove);
        entry.appendChild(row);

        const weight = buildSlider(
            "Weight",
            lora.weight,
            -10,
            10,
            LORA_WEIGHT_STEP,
            (value) => {
                context.debouncedCommit(
                    `stage-${stageIdx}-lora-${loraIdx}-weight`,
                    (clips) => {
                        const target =
                            clips[clipIdx]?.stages[stageIdx]?.loras[loraIdx];
                        if (target) {
                            target.weight = value;
                        }
                    },
                );
            },
            {
                sliderMin: -2,
                sliderMax: 2,
                help: "How strongly this LoRA affects this stage. Negative values invert its effect.",
            },
        );
        weight
            .querySelector<HTMLInputElement>("input.auto-slider-number")
            ?.setAttribute(
                "data-vst-focus-key",
                `stage-${stageIdx}-lora-${loraIdx}-weight`,
            );
        entry.appendChild(weight);
        list.appendChild(entry);
    });

    if (defaults.loraValues.length === 0 && stage.loras.length === 0) {
        const empty = document.createElement("small");
        empty.className = "vst-detail-field-hint";
        empty.textContent = "(no LoRAs available)";
        list.appendChild(empty);
    }

    const add = document.createElement("button");
    add.type = "button";
    add.className = "basic-button small-button vst-add-btn vst-stage-lora-add";
    add.textContent = "+ Add LoRA";
    add.title =
        defaults.loraValues.length > 0
            ? "Add a LoRA to this stage"
            : "No LoRAs are available";
    add.disabled = defaults.loraValues.length === 0;
    add.addEventListener("click", (event) => {
        event.preventDefault();
        context.structuralCommit(
            (clips) => {
                const target = clips[clipIdx]?.stages[stageIdx];
                if (!target || defaults.loraValues.length === 0) {
                    return null;
                }
                target.loras.push({
                    name: defaults.loraValues[0],
                    weight: LORA_WEIGHT_DEFAULT,
                });
                return { kind: "clip", clipIdx, stageIdx };
            },
            { rebuildAfterSelect: true },
        );
    });
    section.appendChild(add);
    return section;
};
