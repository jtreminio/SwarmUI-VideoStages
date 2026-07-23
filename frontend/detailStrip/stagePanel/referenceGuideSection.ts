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
                `R${refIdx}`,
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
    const guideStrength = buildSlider(
        "IC-LoRA Guide Strength",
        stage.controlNetStrength,
        STAGE_CONTROLNET_STRENGTH_MIN,
        STAGE_CONTROLNET_STRENGTH_MAX,
        STAGE_CONTROLNET_STRENGTH_STEP,
        (value) => {
            debouncedCommit("controlnet", (target) => {
                target.controlNetStrength = value;
            });
        },
        {
            help:
                "How strongly this stage is conditioned by the clip's " +
                "IC-LoRA drive video/guides. Higher follows the guide more " +
                "closely; lower gives the model more freedom.",
        },
    );
    tagFocus(guideStrength, "controlnet");
    fields.appendChild(guideStrength);
    if (!icDecision.supported) {
        disableCapabilityControls(guideStrength, icDecision);
    }
};
