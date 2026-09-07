import {
    buildOptionSelect,
    buildUnboundedNumber,
    type OptionSpec,
    tagFocus,
} from "../detailWidgets";
import {
    defaultLoraWeight,
    LORA_WEIGHT_STEP,
    replaceLoraModelAt,
} from "../loraAuthoring";
import { preserveSelectedOption } from "../selectOption";
import type { Clip, RootDefaults } from "../types";
import type { DetailStripContext } from "./context";

export interface ClipLoraControls {
    model: HTMLSelectElement;
    weight: HTMLInputElement;
}

export const buildClipLoraControls = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    loraIdx: number,
    defaults: RootDefaults,
): ClipLoraControls => {
    const lora = clip.loras[loraIdx];
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
    const model = buildOptionSelect(options, lora.name, (value) => {
        context.commit((clips) => {
            const target = clips[clipIdx];
            if (target) {
                const initialWeight = defaultLoraWeight(defaults, value);
                replaceLoraModelAt(target, loraIdx, value, initialWeight);
            }
        });
        context.render();
    });
    model.setAttribute(
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
    return { model, weight };
};
