import { actionButton } from "../../clipEditor/components";
import {
    buildDetailActionButton,
    buildOptionSelect,
    buildUnboundedNumber,
    type OptionSpec,
    tagFocus,
} from "../../detailWidgets";
import { defaultLoraWeight, LORA_WEIGHT_STEP } from "../../loraAuthoring";
import { preserveSelectedOption } from "../../selectOption";
import type { Clip } from "../../types";
import type { StagePanelBindings } from "./types";

export const appendStageLorasSection = ({
    context,
    clip,
    clipIdx,
    stage,
    stageIdx,
    defaults,
    fields,
    commit,
    debouncedCommit,
}: StagePanelBindings): void => {
    const section = document.createElement("section");
    section.className = "vst-stage-loras vst-detail-span-full";
    section.setAttribute("aria-label", `LoRAs for stage S${stageIdx}`);
    const header = document.createElement("div");
    header.className = "vst-stage-loras-header";
    const heading = document.createElement("h5");
    heading.className = "vst-detail-crumb";
    heading.textContent = `LoRAs · ${stage.loras.length}`;
    header.append(heading);
    const structural = (edit: (target: Clip) => void): void =>
        context.structuralCommit((clips) => {
            const target = clips[clipIdx];
            if (!target?.stages[stageIdx]) return null;
            edit(target);
            return "render";
        });
    section.append(header);
    if (stage.loras.length === 0) {
        const empty = document.createElement("p");
        empty.className = "vst-detail-note";
        empty.textContent = "No LoRAs on this stage.";
        section.append(empty);
    }
    const table = document.createElement("table");
    table.className = "vst-stage-lora-table";
    table.setAttribute("aria-label", `LoRAs for stage S${stageIdx}`);
    const columns = table.createTHead().insertRow();
    for (const label of ["LoRA model", "Strength", ""]) {
        const cell = document.createElement("th");
        cell.scope = "col";
        cell.textContent = label;
        if (!label) cell.setAttribute("aria-label", "Remove LoRA");
        columns.append(cell);
    }
    const rows = table.createTBody();
    stage.loras.forEach((lora, loraIdx) => {
        const row = rows.insertRow();
        row.className = "vst-stage-lora-row";
        const options: OptionSpec[] = defaults.loraValues.flatMap(
            (value, index) =>
                value === lora.name ||
                !stage.loras.some((entry) => entry.name === value)
                    ? [{ value, label: defaults.loraLabels[index] ?? value }]
                    : [],
        );
        preserveSelectedOption(options, lora.name, "start", (value) => ({
            value,
            label: `${value} (unsupported persisted value)`,
            disabled: true,
        }));
        const model = tagFocus(
            buildOptionSelect(options, lora.name, (name) => {
                commit((target) => {
                    if (target.loras[loraIdx])
                        target.loras[loraIdx] = {
                            name,
                            weight: defaultLoraWeight(defaults, name),
                        };
                });
                context.render();
            }),
            `lora-${loraIdx}-model`,
        );
        const strength = tagFocus(
            buildUnboundedNumber(lora.weight, LORA_WEIGHT_STEP, (value) => {
                debouncedCommit(`lora-${loraIdx}-strength`, (target) => {
                    if (target.loras[loraIdx])
                        target.loras[loraIdx].weight = value;
                });
            }),
            `lora-${loraIdx}-strength`,
        );
        strength.classList.add("lora-weight-input", "vst-stage-lora-strength");
        model.setAttribute(
            "aria-label",
            `LoRA model ${loraIdx + 1} for stage S${stageIdx}`,
        );
        strength.setAttribute(
            "aria-label",
            `Strength ${loraIdx + 1} for stage S${stageIdx}`,
        );
        row.insertCell().append(model);
        row.insertCell().append(strength);
        row.insertCell().append(
            actionButton({
                key: `remove-lora-${loraIdx}`,
                label: "",
                kind: "delete",
                title: `Remove LoRA ${loraIdx + 1} from stage S${stageIdx}`,
                configure: (button) => {
                    button.classList.add("vst-stage-lora-remove");
                    button.setAttribute("aria-label", button.title);
                },
                run: () =>
                    structural((target) => {
                        target.stages[stageIdx].loras.splice(loraIdx, 1);
                    }),
            }),
        );
    });
    if (stage.loras.length > 0) section.append(table);
    const actions = document.createElement("div");
    actions.className = "vst-editor-actions vst-stage-loras-actions";
    const available = defaults.loraValues.some(
        (name) => !stage.loras.some((entry) => entry.name === name),
    );
    const add = buildDetailActionButton({
        label: "+ Add LoRA",
        title: available
            ? "Add a LoRA to this stage"
            : "All available LoRAs are already on this stage.",
        className: "small-button vst-stage-lora-add",
        disabled: !available,
        onClick: () =>
            structural((target) => {
                const loras = target.stages[stageIdx].loras;
                const name = defaults.loraValues.find(
                    (name) => !loras.some((entry) => entry.name === name),
                );
                if (name)
                    loras.push({
                        name,
                        weight: defaultLoraWeight(defaults, name),
                    });
            }),
    });
    add.dataset.action = "add";
    actions.append(add);
    if (stage.loras.length > 0 && clip.stages.length > 1) {
        actions.append(
            buildDetailActionButton({
                label: "Copy to other stages",
                title: `Replace LoRAs and strengths in every other stage of Clip ${clipIdx}. Later edits stay independent.`,
                className: "small-button vst-stage-loras-copy",
                onClick: () =>
                    structural((target) => {
                        const source = target.stages[stageIdx].loras;
                        target.stages.forEach((other, index) => {
                            if (index !== stageIdx)
                                other.loras = source.map((entry) => ({
                                    ...entry,
                                }));
                        });
                    }),
            }),
        );
    }
    section.append(actions);
    fields.append(section);
};
