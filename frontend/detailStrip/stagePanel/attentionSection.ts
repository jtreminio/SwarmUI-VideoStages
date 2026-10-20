import {
    buildField,
    buildOptionSelect,
    type OptionSpec,
    tagFocus,
} from "../../detailWidgets";
import {
    H3_ATTENTION_WINDOW_FEATURE,
    H3_ATTENTION_WINDOW_MAX_SECONDS,
    H3_ATTENTION_WINDOW_MIN_SECONDS,
    H3_ATTENTION_WINDOW_STEP_SECONDS,
} from "../../h3AttentionWindow";
import { getVideoStagesHostBridge } from "../../host";
import { preserveSelectedOption } from "../../selectOption";
import { getDropdownOptions } from "../../swarmInputs";
import { buildToggleSwitch } from "../../toggleSwitch";
import type { StagePanelBindings } from "./types";

export const appendStageAttentionSection = (
    bindings: StagePanelBindings,
): void => {
    const { context, clip, stage, stageIdx, fields, commit, slider } = bindings;
    if (
        !context
            .authoring()
            .capabilities.forClip(clip)
            .decision("stageAttention").supported
    )
        return;
    const section = document.createElement("section");
    section.className =
        "vst-stage-attention vst-stage-section vst-detail-span-full";
    section.setAttribute("aria-label", `Attention for stage S${stageIdx}`);
    const header = document.createElement("div");
    header.className = "vst-stage-section-header";
    const heading = document.createElement("h5");
    heading.className = "vst-detail-crumb";
    heading.textContent = "Attention";
    header.append(heading);
    const controls = document.createElement("div");
    controls.className = "vst-detail-fields";
    for (const [label, key, enabledKey, paramId] of [
        [
            "Model attention backend",
            "modelAttentionBackend",
            "modelAttentionBackendEnabled",
            "modelattentionbackend",
        ],
        [
            "Use sparse attention",
            "useSparseAttention",
            "useSparseAttentionEnabled",
            "usesparseattention",
        ],
    ] as const) {
        const choices = getDropdownOptions(paramId, `input_${paramId}`);
        const selected = stage[key] ?? choices.values[0] ?? "";
        const options: OptionSpec[] = choices.values.map((value, index) => ({
            value,
            label: choices.labels[index] ?? value,
        }));
        preserveSelectedOption(options, selected, "start", (value) => ({
            value,
            label: `${value} (unsupported persisted value)`,
            disabled: true,
        }));
        let enabled = stage[enabledKey] === true;
        const select = tagFocus(buildOptionSelect(options, selected), key);
        select.dataset.vstHostToggle = "true";
        const field = buildField(
            label,
            select,
            undefined,
            key === "useSparseAttention"
                ? "Sol-Attn uses a training-free threshold (1.3). SLA and VSA keep 10% and require weights trained for their attention pattern; VSA is for FastH3. VSA starts at 0% of sampling; Sol-Attn and SLA start at 20%. Sparse attention stays dense below 12,288 tokens."
                : "Choose the attention implementation for this stage, including the dense fallback when sparse attention is active.",
        );
        field.classList.add("vst-wide-select");
        const toggle = buildToggleSwitch({
            checked: enabled,
            disabled: choices.values.length === 0,
            title: `Enable ${label.toLowerCase()} for stage S${stageIdx}`,
            onChange: (nextEnabled) => {
                if (enabled === nextEnabled) return;
                enabled = nextEnabled;
                doToggleEnable(select.id);
                commit((target) => {
                    target[enabledKey] = enabled;
                    target[key] = select.value;
                });
            },
        });
        toggle
            .querySelector("input")
            ?.setAttribute("id", `${select.id}_toggle`);
        field.addEventListener("change", () => {
            commit((target) => {
                target[key] = select.value;
                target[enabledKey] = enabled;
            });
        });
        const labelElement = field.querySelector("label");
        if (labelElement) {
            const labelRow = document.createElement("div");
            labelRow.className = "vst-field-label-row";
            labelElement.before(labelRow);
            labelRow.append(labelElement, toggle);
        }
        controls.append(field);
    }
    if (
        context.authoring().capabilities.forClip(clip).architectureId ===
            "minimax" &&
        getVideoStagesHostBridge().hasBackendFeature(
            H3_ATTENTION_WINDOW_FEATURE,
        )
    ) {
        const attentionWindow = slider(
            "Attention window (s)",
            "h3AttentionWindowSeconds",
            stage.h3AttentionWindowSeconds,
            H3_ATTENTION_WINDOW_MIN_SECONDS,
            H3_ATTENTION_WINDOW_MAX_SECONDS,
            H3_ATTENTION_WINDOW_STEP_SECONDS,
            (target, value) => {
                target.h3AttentionWindowSeconds = value;
            },
            {
                help: "Total centered temporal attention window for this stage. 0 disables JuanAttn. Dense transformer layers remain fixed at 0,9,19,29,39,49.",
            },
        );
        attentionWindow.dataset.vstH3AttentionWindow = "true";
        controls.append(attentionWindow);
    }
    section.append(header, controls);
    fields.append(section);
};
