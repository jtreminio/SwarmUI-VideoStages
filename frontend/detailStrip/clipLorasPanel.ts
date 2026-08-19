import {
    appendHelp,
    buildAccordionSection,
    buildDetailActionButton,
    buildOptionSelect,
    buildUnboundedNumber,
    type OptionSpec,
    tagFocus,
} from "../detailWidgets";
import {
    appendLoraToClip,
    defaultLoraWeight,
    LORA_WEIGHT_STEP,
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
    const content = document.createDocumentFragment();
    clip.loras.forEach((lora, loraIdx) => {
        const row = document.createElement("div");
        row.className = "vst-clip-lora-entry";
        const remove = buildDetailActionButton({
            label: "×",
            title: `Delete LoRA ${loraIdx}`,
            className: "vst-btn-tiny vst-detail-delete vst-detail-delete-lora",
            variant: "interrupt",
            onClick: () => {
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
        });
        const options: OptionSpec[] = defaults.loraValues.flatMap(
            (value, optionIdx) =>
                value === lora.name ||
                !clip.loras.some(
                    (entry, index) => index !== loraIdx && entry.name === value,
                )
                    ? [
                          {
                              value,
                              label: defaults.loraLabels[optionIdx] ?? value,
                          },
                      ]
                    : [],
        );
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
                }
            });
            context.render();
        });
        select.setAttribute(
            "data-vst-focus-key",
            `clip-${clipIdx}-lora-${loraIdx}-model`,
        );
        const weight = tagFocus(
            buildUnboundedNumber(lora.weight, LORA_WEIGHT_STEP, (value) => {
                context.debouncedCommit(
                    `clip-${clipIdx}-lora-${loraIdx}-weight`,
                    (clips) => {
                        const target = clips[clipIdx]?.loras[loraIdx];
                        if (target) target.weight = value;
                    },
                );
            }),
            `clip-${clipIdx}-lora-${loraIdx}-weight`,
        );
        weight.classList.add("lora-weight-input", "vst-clip-lora-weight");
        row.append(remove, select, weight);
        content.appendChild(row);
    });
    const add = buildDetailActionButton({
        title: nextAvailableName
            ? "Add a LoRA to this clip"
            : "All available LoRAs are already on this clip",
        label: "+ Add LoRA",
        className: "small-button vst-detail-repeating-add vst-detail-add-lora",
        disabled: !nextAvailableName,
        onClick: () => {
            context.structuralCommit(
                (clips) => {
                    const target = clips[clipIdx];
                    const name = nextAvailableName;
                    if (!target || !name) {
                        return null;
                    }
                    appendLoraToClip(
                        target,
                        name,
                        defaultLoraWeight(defaults, name),
                    );
                    return { kind: "clip", clipIdx, stageIdx };
                },
                { rebuildAfterSelect: true },
            );
        },
    });
    content.appendChild(add);
    const built = buildAccordionSection({
        key: `clip-${clipIdx}-loras`,
        label: "LoRAs",
        content,
        counter: clip.loras.length,
        open: clip.loras.length === 0,
        defaultOpen: true,
        className: "vst-detail-loras-section",
    });
    appendHelp(
        built.heading,
        built.section,
        "LoRAs",
        "Each LoRA and weight applies to every stage in this clip.",
    );
    return built.section;
};
