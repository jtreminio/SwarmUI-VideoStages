import {
    appendHelp,
    buildField,
    buildOptionSelect,
    buildRepeatingEditor,
    type OptionSpec,
} from "../detailWidgets";
import {
    appendLoraToClip,
    defaultLoraWeight,
    removeLoraAt,
    replaceLoraModelAt,
} from "../loraAuthoring";
import { preserveSelectedOption } from "../selectOption";
import type { Clip, RootDefaults } from "../types";
import type { DetailStripContext } from "./context";

export const buildClipLorasSection = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    stageIdx: number,
    defaults: RootDefaults,
): HTMLElement => {
    const selectedNames = new Set(clip.loras.map((entry) => entry.name));
    const nextAvailableName = defaults.loraValues.find(
        (value) => !selectedNames.has(value),
    );
    const applySupportedStageWeights = (
        target: Clip,
        loraIdx: number,
        supportedWeight: number,
    ): void => {
        const capabilities = context.capabilities();
        for (const stage of target.stages) {
            if (
                !capabilities.forStage(target, stage).decision("stageLoras")
                    .supported
            ) {
                stage.loraWeights[loraIdx] = 0;
            } else {
                stage.loraWeights[loraIdx] = supportedWeight;
            }
        }
    };
    const items = clip.loras.map((lora, loraIdx) => {
        const editor = document.createElement("div");
        const options: OptionSpec[] = defaults.loraValues
            .filter(
                (value) =>
                    value === lora.name ||
                    !clip.loras.some(
                        (entry, index) =>
                            index !== loraIdx && entry.name === value,
                    ),
            )
            .map((value) => {
                const optionIdx = defaults.loraValues.indexOf(value);
                return {
                    value,
                    label: defaults.loraLabels[optionIdx] ?? value,
                };
            });
        preserveSelectedOption(options, lora.name, "start", (value) => ({
            value,
            label: `${value} (unsupported persisted value)`,
            disabled: true,
        }));
        const select = buildOptionSelect(options, lora.name, (value) => {
            context.commit((clips) => {
                const target = clips[clipIdx];
                if (target) {
                    const initialWeight = defaultLoraWeight(defaults, value);
                    replaceLoraModelAt(target, loraIdx, value, initialWeight);
                    applySupportedStageWeights(target, loraIdx, initialWeight);
                }
            });
            context.render();
        });
        select.setAttribute(
            "data-vst-focus-key",
            `clip-${clipIdx}-lora-${loraIdx}-model`,
        );
        editor.appendChild(buildField("Model", select));
        return {
            label: `L${loraIdx}`,
            groupClassName: "vst-clip-lora-entry",
            editor,
            onDelete: () => {
                context.structuralCommit(
                    (clips) => {
                        const target = clips[clipIdx];
                        if (!target || !removeLoraAt(target, loraIdx)) {
                            return null;
                        }
                        return { kind: "clip", clipIdx, stageIdx };
                    },
                    { rebuildAfterSelect: true },
                );
            },
            deleteTitle: `Delete LoRA ${loraIdx}`,
        };
    });
    const built = buildRepeatingEditor({
        key: `clip-${clipIdx}-loras`,
        label: "LoRAs",
        sectionClass: "vst-detail-loras-section",
        open: items.length > 0,
        defaultActiveIndex: items.length > 0 ? 0 : null,
        items,
        add: {
            title: nextAvailableName
                ? "Add a LoRA to this clip"
                : "All available LoRAs are already on this clip",
            label: "+ Add LoRA",
            className: "vst-detail-add-lora",
            disabled: !nextAvailableName,
            onClick: () => {
                context.structuralCommit(
                    (clips) => {
                        const target = clips[clipIdx];
                        const name = nextAvailableName;
                        if (!target || !name) {
                            return null;
                        }
                        const initialWeight = defaultLoraWeight(defaults, name);
                        appendLoraToClip(target, name, initialWeight);
                        applySupportedStageWeights(
                            target,
                            target.loras.length - 1,
                            initialWeight,
                        );
                        return { kind: "clip", clipIdx, stageIdx };
                    },
                    { rebuildAfterSelect: true },
                );
            },
        },
        remove: {
            title: "Delete LoRA",
            className: "vst-detail-delete-lora",
        },
    });
    appendHelp(
        built.heading,
        built.section,
        "LoRAs",
        "Choose the normal LoRA models once for this clip. Each stage sets its own weight below its reference strengths.",
    );
    return built.section;
};
