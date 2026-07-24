import {
    appendHelp,
    buildField,
    buildOptionSelect,
    buildRepeatingEditor,
    buildSlider,
    type OptionSpec,
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
    const items = stage.loras.map((lora, loraIdx) => {
        const editor = document.createElement("div");
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
        editor.appendChild(buildField("Model", select));

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
        editor.appendChild(weight);
        return {
            label: `LoRA ${loraIdx + 1}`,
            active: loraIdx === 0,
            groupClassName: "vst-stage-lora-entry",
            editor,
            onDelete: () => {
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
            },
            deleteTitle: `Delete LoRA ${loraIdx + 1}`,
        };
    });
    const built = buildRepeatingEditor({
        key: `stage-${stageIdx}-loras`,
        label: "LoRAs",
        sectionClass: "vst-stage-loras",
        // Stage LoRAs do not have their own timeline selection kind. Structural
        // add/remove commits therefore rebuild the owning stage selection; keep
        // the nested editor open while it still has items instead of resetting
        // it to the hard-coded closed state on every rebuild.
        open: items.length > 0,
        items,
        add: {
            title:
                defaults.loraValues.length > 0
                    ? "Add a LoRA to this stage"
                    : "No LoRAs are available",
            label: "+ Add LoRA",
            className: "vst-stage-lora-add",
            disabled: defaults.loraValues.length === 0,
            onClick: () => {
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
            },
        },
        remove: {
            title: "Delete LoRA",
            className: "vst-stage-lora-remove",
        },
    });
    appendHelp(
        built.heading,
        built.section,
        "Stage LoRAs",
        "LoRAs applied only to this stage. A newly added stage copies every LoRA and weight from the previous stage.",
    );
    return built.section;
};
