import { disableCapabilityControls } from "../../detailStrip/capabilityUi";
import type { DetailStripContext } from "../../detailStrip/context";
import {
    appendHelp,
    buildAddButton,
    buildCheckbox,
    buildField,
    buildInstanceRow,
    buildMediaPickRow,
    buildOptionSelect,
    buildStackSection,
} from "../../detailWidgets";
import {
    IC_LORA_ATTENTION_MAX,
    IC_LORA_ATTENTION_MIN,
    IC_LORA_ATTENTION_STEP,
    IC_LORA_SOURCE_STAGE_INPUT,
    IC_LORA_SOURCE_UPLOAD,
    IC_LORA_STAGE_ALL,
    IC_LORA_STRENGTH_MAX,
    IC_LORA_STRENGTH_MIN,
    IC_LORA_STRENGTH_STEP,
} from "../../icLoraAuthoring";
import { preserveSelectedOption } from "../../selectOption";
import type {
    Clip,
    IcLora,
    IcLoraControlType,
    RootDefaults,
} from "../../types";
import {
    clearIcLoraAutoFailure,
    ensureIcLoraAutoWeights,
    IC_LORA_AUTO,
    IC_LORA_AUTO_HINT_ATTR,
    icLoraAutoHint,
} from "./icLoraAutoDownload";
import {
    defaultIcLora,
    isHdrFeature,
    reconcileIcLoraStage,
} from "./icLoraNormalization";
import {
    findIcLoraPreset,
    IC_LORA_PRESET_CUSTOM_ID,
    IC_LORA_PRESET_UNION_CONTROL_ID,
    IC_LORA_PRESETS,
    icLoraRepoUrl,
    icLoraTriggerHint,
} from "./icLoraPresets";

export const buildIcLorasSection = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    defaults: RootDefaults,
): HTMLElement => {
    const { wrap, col } = buildStackSection(
        "IC-LoRAs",
        "vst-detail-iclora-col",
    );
    const sectionLabel = wrap.querySelector<HTMLElement>(".vst-detail-sec");
    if (sectionLabel) {
        appendHelp(
            sectionLabel,
            wrap,
            "IC-LoRAs",
            "In-context LoRAs condition this clip on a drive video or image — " +
                "matching pose, depth, motion, or style. Add one per guide you " +
                "want to apply.",
        );
    }

    const entryAt = (clips: Clip[], entryIdx: number): IcLora | undefined =>
        clips[clipIdx]?.icLoras[entryIdx];
    const clipCapabilities = context.capabilities().forClip(clip);
    const hdrDecision = clipCapabilities.decision("hdr");

    clip.icLoras.forEach((entry, entryIdx) => {
        const { row, fields } = buildInstanceRow({
            rowClass: "vst-detail-iclora",
            indexAttr: "data-vst-iclora-idx",
            index: entryIdx,
            active: false,
            title: `IC-LoRA ${entryIdx + 1}`,
            deleteLabel: "Remove",
            onDelete: () => {
                context.structuralCommit((clips) => {
                    const target = clips[clipIdx];
                    if (!target || entryIdx >= target.icLoras.length) {
                        return null;
                    }
                    target.icLoras.splice(entryIdx, 1);
                    return "render";
                });
            },
            repoint: () => {},
        });

        const persistedHdr = isHdrFeature(entry);
        const presetOptions = IC_LORA_PRESETS.filter(
            (preset) =>
                hdrDecision.supported ||
                !`${preset.id} ${preset.displayName}`
                    .toLowerCase()
                    .includes("hdr"),
        );
        const presetSpecs = [
            { value: IC_LORA_PRESET_CUSTOM_ID, label: "Custom" },
            ...presetOptions.map((preset) => ({
                value: preset.id,
                label: preset.displayName,
            })),
        ];
        preserveSelectedOption(presetSpecs, entry.preset, "start", (value) => ({
            value,
            label: `${value} (unsupported persisted value)`,
            disabled: true,
        }));
        const presetSelect = buildOptionSelect(
            presetSpecs,
            entry.preset,
            (value) => {
                context.commit((clips) => {
                    const target = entryAt(clips, entryIdx);
                    if (!target) {
                        return;
                    }
                    target.preset = value;
                    const preset = findIcLoraPreset(value);
                    if (preset) {
                        target.strength = preset.strength;
                        target.controlType = preset.controlType;
                    }
                });
                clearIcLoraAutoFailure(value);
                context.render();
            },
        );
        fields.appendChild(
            buildField(
                "Preset",
                presetSelect,
                undefined,
                "A curated IC-LoRA setup — picking one fills in the LoRA, " +
                    "strength, and control type for a known effect (pose, " +
                    "depth, style, etc.). Choose Custom to set everything " +
                    "yourself.",
            ),
        );

        const loraSpecs = [
            { value: IC_LORA_AUTO, label: IC_LORA_AUTO },
            ...defaults.loraValues.map((value, optionIdx) => ({
                value,
                label: defaults.loraLabels[optionIdx] ?? value,
            })),
        ];
        preserveSelectedOption(loraSpecs, entry.lora, "start", (value) => ({
            value,
            label: `${value} (unsupported persisted value)`,
            disabled: true,
        }));
        const loraSelect = buildOptionSelect(loraSpecs, entry.lora, (value) => {
            context.commit((clips) => {
                const target = entryAt(clips, entryIdx);
                if (target) {
                    target.lora = value;
                }
            });
            if (value === IC_LORA_AUTO) {
                clearIcLoraAutoFailure(entry.preset);
            }
            context.render();
        });
        fields.appendChild(
            buildField(
                "LoRA",
                loraSelect,
                undefined,
                "The in-context LoRA weights that turn the drive media into " +
                    "conditioning. [AUTO] downloads the preset's recommended " +
                    "weights when they are not installed.",
            ),
        );

        const strength = context.buildClampedNumber({
            key: `iclora-${entryIdx}-strength`,
            value: entry.strength,
            min: IC_LORA_STRENGTH_MIN,
            max: IC_LORA_STRENGTH_MAX,
            step: IC_LORA_STRENGTH_STEP,
            readBack: (clips) => entryAt(clips, entryIdx)?.strength ?? null,
            mutate: (clips, value) => {
                const target = entryAt(clips, entryIdx);
                if (target) {
                    target.strength = value;
                }
            },
        });
        fields.appendChild(
            buildField(
                "Strength",
                strength,
                undefined,
                "How strongly this IC-LoRA steers generation. Higher follows " +
                    "the drive media more closely; too high can overpower the " +
                    "prompt.",
            ),
        );

        const attention = context.buildClampedNumber({
            key: `iclora-${entryIdx}-attention`,
            value: entry.attentionStrength,
            min: IC_LORA_ATTENTION_MIN,
            max: IC_LORA_ATTENTION_MAX,
            step: IC_LORA_ATTENTION_STEP,
            readBack: (clips) =>
                entryAt(clips, entryIdx)?.attentionStrength ?? null,
            mutate: (clips, value) => {
                const target = entryAt(clips, entryIdx);
                if (target) {
                    target.attentionStrength = value;
                }
            },
        });
        fields.appendChild(
            buildField(
                "Attention",
                attention,
                undefined,
                "Scales how much the IC-LoRA influences the model's attention " +
                    "layers. A finer control than Strength; leave at the " +
                    "default unless a preset tunes it.",
            ),
        );

        const preset = findIcLoraPreset(entry.preset);
        if (!preset || preset.id === IC_LORA_PRESET_UNION_CONTROL_ID) {
            const controlSelect = buildOptionSelect(
                [
                    { value: "none", label: "None (raw video)" },
                    { value: "canny", label: "Canny edges" },
                    { value: "depth", label: "Depth map" },
                    { value: "normal", label: "Normal map" },
                ],
                entry.controlType,
                (value) => {
                    context.commit((clips) => {
                        const target = entryAt(clips, entryIdx);
                        if (target) {
                            target.controlType = value as IcLoraControlType;
                        }
                    });
                },
            );
            fields.appendChild(
                buildField(
                    "Control",
                    controlSelect,
                    undefined,
                    "Preprocesses the drive video into a control signal before " +
                        "conditioning: Canny edges, a depth map, or a normal " +
                        "map. None feeds the raw video straight in.",
                ),
            );
        }

        const applySelect = buildOptionSelect(
            [
                { value: `${IC_LORA_STAGE_ALL}`, label: "All stages" },
                ...clip.stages.map((_, stageIdx) => ({
                    value: `${stageIdx}`,
                    label: `Stage ${stageIdx}`,
                })),
            ],
            `${entry.stage}`,
            (value) => {
                context.commit((clips) => {
                    const target = entryAt(clips, entryIdx);
                    if (!target) {
                        return;
                    }
                    const stage = Number(value);
                    target.stage =
                        Number.isInteger(stage) && stage >= 0
                            ? stage
                            : IC_LORA_STAGE_ALL;
                    reconcileIcLoraStage(target, !!clips[clipIdx]?.sourceVideo);
                });
                context.render();
            },
        );
        fields.appendChild(
            buildField(
                "Apply on",
                applySelect,
                undefined,
                "Which stage this IC-LoRA conditions — a single stage, or All " +
                    "stages of the clip.",
            ),
        );

        if (
            (entry.stage >= 1 || !!clip.sourceVideo) &&
            (entry.source === IC_LORA_SOURCE_UPLOAD ||
                entry.source === IC_LORA_SOURCE_STAGE_INPUT)
        ) {
            const sourceSelect = buildOptionSelect(
                [
                    { value: IC_LORA_SOURCE_UPLOAD, label: "Upload" },
                    {
                        value: IC_LORA_SOURCE_STAGE_INPUT,
                        label: "Stage input",
                    },
                ],
                entry.source,
                (value) => {
                    context.commit((clips) => {
                        const target = entryAt(clips, entryIdx);
                        if (target) {
                            target.source = value;
                        }
                    });
                    context.render();
                },
            );
            fields.appendChild(
                buildField(
                    "Source",
                    sourceSelect,
                    undefined,
                    "Where the drive media comes from: Upload your own " +
                        "video/image, or Stage input to drive from the frames " +
                        "already entering this stage.",
                ),
            );
        }

        if (entry.source === IC_LORA_SOURCE_STAGE_INPUT) {
            const hint = document.createElement("small");
            hint.className = "vst-audio-field-hint";
            hint.textContent =
                entry.stage >= 1
                    ? `Driven by stage ${entry.stage}'s input (the previous stage's output).`
                    : "Driven by each stage's incoming frames (the source footage on stage 0).";
            fields.appendChild(hint);
        } else if (entry.source === IC_LORA_SOURCE_UPLOAD) {
            fields.appendChild(
                buildMediaPickRow(
                    "Drive Media",
                    "video/*,image/*",
                    ["image", "video"],
                    entry.video?.fileName,
                    (data, fileName) => {
                        context.commit((clips) => {
                            const target = entryAt(clips, entryIdx);
                            if (target) {
                                target.video = { data, fileName };
                            }
                        });
                        context.render();
                    },
                    () => {
                        context.commit((clips) => {
                            const target = entryAt(clips, entryIdx);
                            if (target) {
                                target.video = null;
                            }
                        });
                        context.render();
                    },
                ),
            );
            if (!entry.video?.data && !!clip.sourceVideo) {
                const hint = document.createElement("small");
                hint.className = "vst-audio-field-hint";
                hint.textContent =
                    "No upload — drives from the stage's incoming footage.";
                fields.appendChild(hint);
            }
            if (entry.video?.data?.startsWith("data:video/")) {
                fields.appendChild(
                    buildCheckbox(
                        "Voice ref from drive audio",
                        entry.driveAudioRef === true,
                        (value) => {
                            context.commit((clips) => {
                                const target = entryAt(clips, entryIdx);
                                if (target) {
                                    target.driveAudioRef = value;
                                }
                            });
                        },
                        {
                            help:
                                "Use this drive video's audio as the speaker sample " +
                                "(LipDub): new speech matching the prompt is " +
                                "generated in that voice.",
                        },
                    ),
                );
            }
        } else {
            const slot = document.createElement("small");
            slot.className = "vst-audio-field-hint";
            slot.textContent = `Driven by ${entry.source} (legacy source)`;
            fields.appendChild(slot);
        }

        const hintText = [preset?.note ?? "", icLoraTriggerHint(preset)]
            .filter(Boolean)
            .join(" ");
        if (hintText || preset) {
            const hint = document.createElement("small");
            hint.className = "vst-audio-field-hint";
            hint.textContent = hintText ? `${hintText} ` : "";
            if (preset) {
                const link = document.createElement("a");
                link.href = icLoraRepoUrl(preset);
                link.target = "_blank";
                link.rel = "noopener";
                link.textContent = "repo";
                hint.appendChild(link);
            }
            fields.appendChild(hint);
        }

        if (!persistedHdr || hdrDecision.supported) {
            ensureIcLoraAutoWeights(entry, defaults.loraValues, context.render);
        }
        const autoText = icLoraAutoHint(entry, defaults.loraValues);
        if (autoText) {
            const autoHint = document.createElement("small");
            autoHint.className = "vst-audio-field-hint";
            if (preset) {
                autoHint.setAttribute(IC_LORA_AUTO_HINT_ATTR, preset.id);
            }
            autoHint.textContent = autoText;
            fields.appendChild(autoHint);
        }
        if (persistedHdr && !hdrDecision.supported) {
            disableCapabilityControls(row, hdrDecision, [".vst-detail-delete"]);
        }
        col.appendChild(row);
    });

    col.appendChild(
        buildAddButton("+ Add IC-LoRA", "vst-detail-add-iclora", () => {
            context.structuralCommit((clips) => {
                const target = clips[clipIdx];
                if (
                    !target ||
                    !context.capabilities().forClip(target).decision("icLora")
                        .supported
                ) {
                    return null;
                }
                target.icLoras.push(
                    defaultIcLora({
                        lora: defaults.loraValues[0] ?? IC_LORA_AUTO,
                    }),
                );
                return "render";
            });
        }),
    );
    return wrap;
};
