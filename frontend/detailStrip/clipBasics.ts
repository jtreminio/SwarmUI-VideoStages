import type { AuthoringState } from "../architectures/policy";
import { clipLengthReferenceIndex } from "../clipReferenceAuthoring";
import { CLIP_DURATION_MAX, CLIP_DURATION_MIN } from "../constants";
import {
    buildField,
    buildNumber,
    buildOptionSelect,
    buildSlider,
    type SectionHeaderAction,
} from "../detailWidgets";
import {
    H3_TEXT_ENCODER_FEATURE,
    H3_TEXT_ENCODERS,
} from "../generatedMiniMaxTextEncoder";
import {
    H3_ATTENTION_WINDOW_MAX_SECONDS,
    H3_ATTENTION_WINDOW_MIN_SECONDS,
    H3_ATTENTION_WINDOW_STEP_SECONDS,
} from "../h3AttentionWindow";
import { normalizeH3TextEncoder } from "../h3TextEncoder";
import { getVideoStagesHostBridge } from "../host";
import { installHostFeature } from "../host/swarmUiAdapters";
import { skipGlyph, skipTitle } from "../skipVocabulary";
import { applyClipDurationResize } from "../timelineEdit";
import type { Clip, ReferenceFraming } from "../types";
import { applyPersistedCapabilityRepair } from "./capabilityUi";
import type { DetailStripContext } from "./context";

const DURATION_STEP = 0.1;
const REFERENCE_LENGTH_HINT = "(derived from a reference's media length)";
const CLIP_PROJ_REPO = "https://github.com/nicolab28/ComfyUI-ClipProj";

const appendClipProjRepoLink = (field: HTMLElement): void => {
    const help = field.querySelector<HTMLElement>(".sui-info-popover");
    if (!help) {
        return;
    }
    const repo = document.createElement("a");
    repo.href = CLIP_PROJ_REPO;
    repo.target = "_blank";
    repo.rel = "noopener noreferrer";
    repo.textContent = "ComfyUI-ClipProj";
    help.append(repo, ".");
};

export const buildClipColumn = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    referenceFramingState?: AuthoringState,
    showH3AttentionWindow = false,
    h3TextEncoderState: "hidden" | "install" | "ready" = "hidden",
): HTMLElement => {
    const column = document.createElement("div");
    column.className =
        "input-group-content vst-detail-section-content vst-detail-col vst-detail-clip";

    const initVideoClip = !!clip.initVideo;
    const lengthReferenceIdx = clipLengthReferenceIndex(clip.references);
    const lengthDerived =
        clip.clipLengthFromAudio === true ||
        clip.clipLengthFromControlNet === true ||
        lengthReferenceIdx >= 0 ||
        initVideoClip;
    const durationInput = buildNumber(
        clip.duration,
        CLIP_DURATION_MIN,
        CLIP_DURATION_MAX,
        DURATION_STEP,
        (value) => {
            context.debouncedCommit("duration", (clips) => {
                const target = clips[clipIdx];
                if (target && !lengthDerived) {
                    const defaults = context.authoring().defaults;
                    applyClipDurationResize(target, value, defaults);
                }
            });
        },
    );
    durationInput.setAttribute("data-vst-focus-key", "duration");
    const durationHint = !lengthDerived
        ? null
        : initVideoClip
          ? "(derived from the source video range)"
          : lengthReferenceIdx >= 0
            ? REFERENCE_LENGTH_HINT
            : "(derived from audio/ControlNet source)";
    const durationField = buildField(
        "Duration (s)",
        durationInput,
        durationHint ?? REFERENCE_LENGTH_HINT,
    );
    durationField
        .querySelector(".vst-detail-field-hint")
        ?.classList.toggle("vst-detail-field-hint-hidden", !durationHint);
    if (lengthDerived) {
        durationInput.disabled = true;
        durationField.classList.add("vst-field-disabled");
    }
    column.appendChild(durationField);
    if (referenceFramingState?.visible) {
        const framing = buildOptionSelect(
            [
                { value: "crop", label: "Crop" },
                { value: "stretch", label: "Stretch" },
                { value: "fit", label: "Fit (black padding)" },
                { value: "fit-green", label: "Fit (green padding)" },
            ],
            clip.refFraming,
            (value) => {
                context.commit((clips) => {
                    const target = clips[clipIdx];
                    if (target) {
                        target.refFraming = value as ReferenceFraming;
                    }
                });
            },
        );
        framing.dataset.vstReferenceFraming = "true";
        const field = buildField(
            "Reference resize",
            framing,
            undefined,
            "Fit (green padding) preserves aspect ratio and pads with #66FF00 so outpainting IC-LoRAs treat the padded area as empty.",
        );
        if (!referenceFramingState.enabled) {
            applyPersistedCapabilityRepair(field, referenceFramingState, {
                repair: {
                    label: "Reset reference resize",
                    className: "vst-reset-unsupported-reference-framing",
                    onRepair: () => {
                        context.commit((clips) => {
                            const target = clips[clipIdx];
                            if (target) {
                                target.refFraming = "crop";
                            }
                        });
                    },
                },
            });
        }
        column.appendChild(field);
    }
    if (showH3AttentionWindow) {
        const attentionWindow = buildSlider(
            "Attention window (s)",
            clip.h3AttentionWindowSeconds,
            H3_ATTENTION_WINDOW_MIN_SECONDS,
            H3_ATTENTION_WINDOW_MAX_SECONDS,
            H3_ATTENTION_WINDOW_STEP_SECONDS,
            (value) => {
                context.debouncedCommit("h3AttentionWindowSeconds", (clips) => {
                    const target = clips[clipIdx];
                    if (target) {
                        target.h3AttentionWindowSeconds = value;
                    }
                });
            },
            {
                hint: "0 disables JuanAttn for this clip.",
                help: "Total centered temporal attention window. Dense transformer layers remain fixed at 0,9,19,29,39,49.",
            },
        );
        attentionWindow.dataset.vstH3AttentionWindow = "true";
        column.appendChild(attentionWindow);
    }
    if (h3TextEncoderState === "ready") {
        const textEncoder = buildOptionSelect(
            H3_TEXT_ENCODERS.map((value) => ({ value, label: value })),
            clip.h3TextEncoder,
            (value) => {
                context.commit((clips) => {
                    const target = clips[clipIdx];
                    if (target) {
                        target.h3TextEncoder = normalizeH3TextEncoder(value);
                    }
                });
            },
        );
        textEncoder.dataset.vstH3TextEncoder = "true";
        const textEncoderField = buildField(
            "Text Encoder",
            textEncoder,
            undefined,
            "Default uses MiniMax H3's full 32B encoder. The 8B and 4B options use less VRAM; VideoStages will download the matching projection automatically. Requires ",
        );
        appendClipProjRepoLink(textEncoderField);
        column.appendChild(textEncoderField);
    } else if (h3TextEncoderState === "install") {
        const install = document.createElement("button");
        install.type = "button";
        install.className = "basic-button";
        install.dataset.vstInstallClipproj = "true";
        install.textContent = "Install ComfyUI-ClipProj";
        const installField = buildField(
            "Text Encoder",
            install,
            undefined,
            "Install the custom node required for MiniMax H3's smaller text encoders. SwarmUI will restart managed ComfyUI backends. See ",
        );
        installField.id = `vst_clip_${clipIdx}_clipproj_install`;
        appendClipProjRepoLink(installField);
        install.addEventListener("click", () => {
            if (!installHostFeature(H3_TEXT_ENCODER_FEATURE, installField.id)) {
                getVideoStagesHostBridge().showError(
                    "SwarmUI's ComfyUI feature installer is unavailable.",
                );
            }
        });
        column.appendChild(installField);
    }
    return column;
};

export const buildClipSkipAction = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
): SectionHeaderAction => ({
    label: skipGlyph(clip.skipped === true),
    title: skipTitle("clip", clip.skipped === true),
    className: "vst-detail-skip-clip",
    active: clip.skipped === true,
    onClick: () => context.toggleClipSkip(clipIdx),
});
