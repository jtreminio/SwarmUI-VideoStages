import {
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_STEP,
} from "../../constants";
import { buildSlider, tagFocus } from "../../detailWidgets";
import {
    STAGE_CONTROLNET_STRENGTH_MAX,
    STAGE_CONTROLNET_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_STEP,
} from "../../icLoraAuthoring";
import { refSourceLabel } from "../../timelineDetail";
import {
    buildCapabilityNotice,
    disableCapabilityControls,
} from "../capabilityUi";
import type { StagePanelBindings } from "./types";

export const appendStageReferenceGuideSection = ({
    context,
    clip,
    clipIdx,
    stage,
    stageIdx,
    fields,
    debouncedCommit,
}: StagePanelBindings): void => {
    if (clip.refs.length > 0) {
        const refDecision = context
            .capabilities()
            .forClip(clip)
            .decision("frameReferences");
        const refsHeader = document.createElement("div");
        refsHeader.className = "vst-detail-sec vst-detail-span-full";
        refsHeader.textContent = "Reference Strengths";
        fields.appendChild(refsHeader);
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

    if (clip.icLoras.length === 0) return;
    const icDecision = context.capabilities().forClip(clip).decision("icLora");
    clip.icLoras.forEach((entry, entryIdx) => {
        if (entry.stage >= 0 && entry.stage !== stageIdx) {
            return;
        }
        const guideStrength = buildSlider(
            `IC-LoRA Strength ${entryIdx}`,
            stage.icLoraStrengths[entryIdx] ?? stage.controlNetStrength,
            STAGE_CONTROLNET_STRENGTH_MIN,
            STAGE_CONTROLNET_STRENGTH_MAX,
            STAGE_CONTROLNET_STRENGTH_STEP,
            (value) => {
                debouncedCommit(`ic-lora-strength-${entryIdx}`, (target) => {
                    target.icLoraStrengths[entryIdx] = value;
                });
            },
        );
        tagFocus(guideStrength, `ic-lora-strength-${entryIdx}`);
        fields.appendChild(guideStrength);
        if (!icDecision.supported) {
            disableCapabilityControls(guideStrength, icDecision);
        }
    });
};
