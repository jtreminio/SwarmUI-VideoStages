import { architectureIcLoraDisplayName } from "../../architectures/behaviorRegistry";
import {
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_STEP,
} from "../../constants";
import {
    buildField,
    buildNumber,
    buildSlider,
    buildUnboundedNumber,
    tagFocus,
} from "../../detailWidgets";
import {
    STAGE_CONTROLNET_STRENGTH_MAX,
    STAGE_CONTROLNET_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_STEP,
} from "../../icLoraAuthoring";
import { LORA_WEIGHT_STEP } from "../../loraAuthoring";
import { refSourceLabel } from "../../timelineDetail";
import {
    buildCapabilityNotice,
    disableCapabilityControls,
} from "../capabilityUi";
import type { StagePanelBindings } from "./types";

const appendSectionHeader = (fields: HTMLElement, label: string): void => {
    const header = document.createElement("div");
    header.className =
        "vst-detail-sec vst-detail-span-full vst-detail-crumb vst-detail-subsection-crumb";
    header.setAttribute("role", "heading");
    header.setAttribute("aria-level", "4");
    header.textContent = label;
    fields.appendChild(header);
};

const shortModelName = (modelName: string): string => {
    const parts = modelName.split(/[\\/]/);
    return parts[parts.length - 1] || modelName;
};

export const appendStageReferenceGuideSection = ({
    context,
    clip,
    clipIdx,
    stage,
    stageIdx,
    fields,
    stageCapabilities,
    debouncedCommit,
}: StagePanelBindings): void => {
    if (clip.refs.length > 0) {
        const refDecision = context
            .capabilities()
            .forClip(clip)
            .decision("frameReferences");
        appendSectionHeader(fields, "Reference Strengths");
        const setRefHover = (refIdx: number, on: boolean): void => {
            context
                .getBoundBody()
                ?.querySelector<HTMLElement>(
                    `.vst-refs-mark[data-clip-idx="${clipIdx}"][data-ref-idx="${refIdx}"]`,
                )
                ?.classList.toggle("vst-ref-hover", on);
        };
        clip.refs.forEach((ref, refIdx) => {
            const current =
                refIdx < stage.refStrengths.length
                    ? stage.refStrengths[refIdx]
                    : STAGE_REF_STRENGTH_MAX;
            const refSlider = buildSlider(
                `Reference R${refIdx}`,
                current,
                STAGE_REF_STRENGTH_MIN,
                STAGE_REF_STRENGTH_MAX,
                STAGE_REF_STRENGTH_STEP,
                (value) => {
                    debouncedCommit(`refstrength-${refIdx}`, (target) => {
                        if (refIdx < target.refStrengths.length) {
                            target.refStrengths[refIdx] = value;
                        }
                    });
                },
                {
                    title: `${refSourceLabel(ref.source ?? "")} · frame ${ref.frame ?? 0}${ref.fromEnd ? " (from end)" : ""}`,
                },
            );
            refSlider.classList.add("vst-stage-ref-slider");
            tagFocus(refSlider, `ref-${refIdx}`);
            refSlider.addEventListener("mouseenter", () =>
                setRefHover(refIdx, true),
            );
            refSlider.addEventListener("mouseleave", () =>
                setRefHover(refIdx, false),
            );
            fields.appendChild(refSlider);
            if (!refDecision.supported) {
                disableCapabilityControls(refSlider, refDecision);
            }
        });
        if (!refDecision.supported) {
            fields.appendChild(buildCapabilityNotice(refDecision));
        }
    }

    if (clip.loras.length > 0) {
        appendSectionHeader(fields, "LoRA Weights");
        const loraState = stageCapabilities.authoringState(
            "stageLoras",
            clip.loras.length > 0,
        );
        const group = document.createDocumentFragment();
        clip.loras.forEach((entry, entryIdx) => {
            const weight = tagFocus(
                buildUnboundedNumber(
                    stage.loraWeights[entryIdx] ?? 1,
                    LORA_WEIGHT_STEP,
                    (value) => {
                        debouncedCommit(`lora-weight-${entryIdx}`, (target) => {
                            target.loraWeights[entryIdx] = value;
                        });
                    },
                ),
                `lora-weight-${entryIdx}`,
            );
            weight.classList.add("lora-weight-input", "vst-stage-lora-weight");
            const row = buildField(shortModelName(entry.name), weight);
            row.classList.add("vst-stage-lora-weight-row");
            row.title = entry.name;
            group.appendChild(row);
        });
        if (!loraState.enabled) {
            disableCapabilityControls(group, loraState);
        }
        fields.appendChild(group);
    }

    const applicableIcLoras = clip.icLoras
        .map((entry, entryIdx) => ({ entry, entryIdx }))
        .filter(({ entry }) => entry.stage < 0 || entry.stage === stageIdx);
    if (applicableIcLoras.length === 0) return;

    appendSectionHeader(fields, "IC-LoRA Guide Strengths");
    const icDecision = context.capabilities().forClip(clip).decision("icLora");
    const icGroup = document.createDocumentFragment();
    applicableIcLoras.forEach(({ entry, entryIdx }) => {
        const displayName = architectureIcLoraDisplayName(
            clip.architecture,
            entry,
        );
        const guideStrength = tagFocus(
            buildNumber(
                stage.icLoraStrengths[entryIdx] ?? 1,
                STAGE_CONTROLNET_STRENGTH_MIN,
                STAGE_CONTROLNET_STRENGTH_MAX,
                STAGE_CONTROLNET_STRENGTH_STEP,
                (value) => {
                    debouncedCommit(
                        `ic-lora-strength-${entryIdx}`,
                        (target) => {
                            target.icLoraStrengths[entryIdx] = value;
                        },
                    );
                },
            ),
            `ic-lora-strength-${entryIdx}`,
        );
        guideStrength.classList.add(
            "lora-weight-input",
            "vst-stage-iclora-strength",
        );
        const row = buildField(shortModelName(displayName), guideStrength);
        row.classList.add("vst-stage-iclora-strength-row");
        row.title = displayName;
        icGroup.appendChild(row);
    });
    if (!icDecision.supported) {
        disableCapabilityControls(icGroup, icDecision);
    }
    fields.appendChild(icGroup);
};
