import type { AuthoringState } from "../architectures/policy";
import { clipLengthReferenceIndex } from "../clipReferenceAuthoring";
import { setAudioOnlyLead } from "../clipRelationships";
import { CLIP_DURATION_MAX, CLIP_DURATION_MIN } from "../constants";
import {
    buildCheckbox,
    buildField,
    buildOptionSelect,
    buildSlider,
} from "../detailWidgets";
import { H3_REFERENCE_IMAGE_SIZES } from "../generatedMiniMaxReferenceImageSize";
import {
    H3_TEXT_ENCODER_FEATURE,
    H3_TEXT_ENCODERS,
} from "../generatedMiniMaxTextEncoder";
import { normalizeH3ReferenceImageSize } from "../h3ReferenceImageSize";
import { normalizeH3TextEncoder } from "../h3TextEncoder";
import { getVideoStagesHostBridge } from "../host";
import { installHostFeature } from "../host/swarmUiAdapters";
import { applyClipDurationResize } from "../timelineEdit";
import type { Clip, ReferenceFraming } from "../types";
import { applyPersistedCapabilityRepair } from "./capabilityUi";
import type { DetailStripContext } from "./context";

const DURATION_STEP = 0.5;
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
    showH3ReferenceImageSize = false,
    h3TextEncoderState: "hidden" | "install" | "ready" = "hidden",
    supportsAudioOnly = false,
): HTMLElement => {
    const column = document.createElement("div");
    column.className =
        "input-group-content vst-detail-section-content vst-detail-col vst-detail-clip";
    const audioOnly = clipIdx === 0 && clip.audioOnly === true;

    const initVideoClip = !!clip.initVideo;
    const lengthReferenceIdx = clipLengthReferenceIndex(clip.references);
    const lengthDerived =
        clip.clipLengthFromAudio === true ||
        clip.clipLengthFromControlNet === true ||
        lengthReferenceIdx >= 0 ||
        initVideoClip;
    const durationHint = !lengthDerived
        ? null
        : initVideoClip
          ? "(derived from the source video range)"
          : lengthReferenceIdx >= 0
            ? REFERENCE_LENGTH_HINT
            : "(derived from audio/ControlNet source)";
    const durationField = buildSlider(
        "Duration (s)",
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
        {
            sliderMax: 20,
            hint: durationHint ?? REFERENCE_LENGTH_HINT,
        },
    );
    const durationInput = durationField.querySelector<HTMLInputElement>(
        "input.auto-slider-number",
    );
    if (!durationInput) {
        throw new Error("duration slider number input missing");
    }
    durationInput.setAttribute("data-vst-focus-key", "duration");
    durationField
        .querySelector(".vst-detail-field-hint")
        ?.classList.toggle("vst-detail-field-hint-hidden", !durationHint);
    if (lengthDerived) {
        durationField
            .querySelectorAll<HTMLInputElement>("input")
            .forEach((input) => {
                input.disabled = true;
            });
        durationField.classList.add("vst-field-disabled");
    }
    column.appendChild(durationField);
    column.appendChild(
        buildCheckbox(
            "Use SeedVR",
            clip.useSeedVr,
            (value) => {
                context.commit((clips) => {
                    const target = clips[clipIdx];
                    if (target) {
                        target.useSeedVr = value;
                    }
                });
            },
            { disabled: audioOnly },
        ),
    );
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
    if (showH3ReferenceImageSize) {
        const referenceImageSize = buildOptionSelect(
            H3_REFERENCE_IMAGE_SIZES.map((value) => ({
                value,
                label:
                    value === "match"
                        ? "Match generation"
                        : "Maximum detail (slower)",
            })),
            clip.h3ReferenceImageSize,
            (value) => {
                context.commit((clips) => {
                    const target = clips[clipIdx];
                    if (target) {
                        target.h3ReferenceImageSize =
                            normalizeH3ReferenceImageSize(value);
                    }
                });
            },
        );
        referenceImageSize.dataset.vstH3ReferenceImageSize = "true";
        column.appendChild(
            buildField(
                "Reference image size",
                referenceImageSize,
                undefined,
                "Applies to every Picture reference in this clip, including Make Them Bonita. Match generation limits each image to the generation's pixel area. Maximum keeps source resolution up to a 2048px short edge and increases VRAM and sampling time.",
            ),
        );
    }
    if (supportsAudioOnly && clipIdx === 0) {
        column.appendChild(
            buildCheckbox(
                "Audio Only",
                clip.audioOnly === true,
                (value) => {
                    context.structuralCommit((clips) => {
                        const transaction = context.authoring();
                        return setAudioOnlyLead(
                            clips,
                            value,
                            transaction,
                            transaction.defaults.fps,
                        )
                            ? "render"
                            : null;
                    });
                },
                { disabled: clip.initVideo !== null },
            ),
        );
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
