import { appendHelp, buildNumber, buildSelect } from "../detailWidgets";
import { stageChipLabel } from "../timelineDetail";
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
    section.className = "vst-audio-field vst-stage-loras vst-detail-span-full";
    const label = document.createElement("div");
    label.className = "vst-detail-sec";
    label.textContent = `LoRAs — Stage ${stageChipLabel(stageIdx)}`;
    appendHelp(
        label,
        section,
        "Stage LoRAs",
        "LoRAs applied to this stage only, on top of the model. Each row " +
            "picks a LoRA and its weight (negative weights invert its effect).",
    );
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
    stage.loras.forEach((lora, index) => {
        const row = document.createElement("div");
        row.className = "vst-stage-lora-row";
        const select = buildSelect(
            defaults.loraValues,
            defaults.loraLabels,
            lora.name,
            (value) => {
                context.commit((clips) => {
                    const entry =
                        clips[clipIdx]?.stages[stageIdx]?.loras[index];
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
                context.debouncedCommit(`lora-${index}-weight`, (clips) => {
                    const entry =
                        clips[clipIdx]?.stages[stageIdx]?.loras[index];
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
            context.structuralCommit((clips) => {
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

    const addButton = document.createElement("button");
    addButton.type = "button";
    addButton.className = "basic-button small-button vst-stage-lora-add";
    addButton.textContent = "+ Add LoRA";
    addButton.addEventListener("click", () => {
        context.structuralCommit((clips) => {
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
    section.appendChild(addButton);
    return section;
};
