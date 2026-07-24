import { disableCapabilityControls } from "../../detailStrip/capabilityUi";
import type { DetailStripContext } from "../../detailStrip/context";
import {
    appendHelp,
    buildField,
    buildMediaPickRow,
    buildOptionSelect,
    buildRepeatingEditor,
} from "../../detailWidgets";
import {
    IC_LORA_ATTENTION_MAX,
    IC_LORA_ATTENTION_MIN,
    IC_LORA_ATTENTION_STEP,
    IC_LORA_SOURCE_INCOMING,
    IC_LORA_SOURCE_UPLOAD,
    IC_LORA_STAGE_ALL,
    IC_LORA_STRENGTH_MAX,
    IC_LORA_STRENGTH_MIN,
    IC_LORA_STRENGTH_STEP,
} from "../../icLoraAuthoring";
import {
    appendIcLoraStrengthToClip,
    removeIcLoraStrengthAt,
} from "../../normalizationStage";
import { getClips } from "../../persistence/repository";
import { setSelection } from "../../selection";
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
import { canUseIncomingIcLoraDrive } from "./icLoraDriveAvailability";
import {
    canonicalizeIcLoraFields,
    defaultIcLora,
    isHdrFeature,
} from "./icLoraNormalization";
import {
    findIcLoraPreset,
    IC_LORA_PRESET_CUSTOM_ID,
    IC_LORA_PRESETS,
    icLoraDriveMediaContract,
    icLoraDriveMediaContractForData,
    icLoraRepoUrl,
    icLoraTriggerHint,
} from "./icLoraPresets";

export const buildIcLorasSection = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    defaults: RootDefaults,
    selectedEntryIdx: number | null = null,
    open = selectedEntryIdx !== null,
): HTMLElement => {
    const clipCapabilities = context.capabilities().forClip(clip);
    const icLoraDecision = clipCapabilities.decision("icLora");
    const entryIdx =
        clip.icLoras.length === 0
            ? null
            : Math.max(
                  0,
                  Math.min(selectedEntryIdx ?? 0, clip.icLoras.length - 1),
              );
    const buildSection = (
        editorForItem?: (index: number) => HTMLElement | undefined,
    ): HTMLElement => {
        const built = buildRepeatingEditor({
            key: "ic-loras",
            label: "IC-LoRAs",
            sectionClass: "vst-detail-iclora-section",
            open,
            items: clip.icLoras.map((_, index) => ({
                label: `IC${index}`,
                focusKey: `ic-lora-tab-${index}`,
                title: `Edit IC-LoRA ${index}`,
                active: index === entryIdx,
                className: "vst-iclora-tab",
                onSelect: () =>
                    setSelection({
                        kind: "ic-lora",
                        clipIdx,
                        entryIdx: index,
                    }),
                onDelete: () => {
                    context.structuralCommit(
                        (clips) => {
                            const target = clips[clipIdx];
                            if (!target?.icLoras[index]) {
                                return null;
                            }
                            target.icLoras.splice(index, 1);
                            removeIcLoraStrengthAt(target, index);
                            return target.icLoras.length > 0
                                ? {
                                      kind: "ic-lora",
                                      clipIdx,
                                      entryIdx: Math.min(
                                          index,
                                          target.icLoras.length - 1,
                                      ),
                                  }
                                : { kind: "clip", clipIdx, stageIdx: 0 };
                        },
                        { rebuildAfterSelect: true },
                    );
                },
            })),
            add: {
                title: icLoraDecision.supported
                    ? "Add an IC-LoRA"
                    : icLoraDecision.reason,
                label: "+ Add IC-LoRA",
                className: "vst-detail-add-iclora",
                disabled: !icLoraDecision.supported,
                onClick: () => {
                    context.structuralCommit((clips) => {
                        const target = clips[clipIdx];
                        if (
                            !target ||
                            !context
                                .capabilities()
                                .forClip(target)
                                .decision("icLora").supported
                        ) {
                            return null;
                        }
                        target.icLoras.push(
                            defaultIcLora({
                                lora: IC_LORA_AUTO,
                            }),
                        );
                        appendIcLoraStrengthToClip(target);
                        return {
                            kind: "ic-lora",
                            clipIdx,
                            entryIdx: target.icLoras.length - 1,
                        };
                    });
                },
            },
            remove: {
                title:
                    entryIdx === null
                        ? "No IC-LoRA to delete"
                        : `Delete IC-LoRA ${entryIdx}`,
                className: "vst-detail-delete-iclora",
            },
            editorForItem,
        });
        appendHelp(
            built.heading,
            built.section,
            "IC-LoRAs",
            "In-context LoRAs use uploaded or incoming media for pose, depth, " +
                "motion, style, audio, or other preset-specific conditioning. " +
                "Add one per guide you want to apply.",
        );
        return built.section;
    };
    if (entryIdx === null) {
        return buildSection();
    }

    const buildEditor = (editorEntryIdx: number): HTMLElement | undefined => {
        const entry = clip.icLoras[editorEntryIdx];
        if (!entry) {
            return undefined;
        }
        const entryIdx = editorEntryIdx;
        const col = document.createElement("div");
        col.className =
            "vst-detail-col vst-detail-instance-fields vst-detail-iclora vst-detail-iclora-col";
        col.setAttribute("data-vst-iclora-idx", `${entryIdx}`);
        const entryAt = (clips: Clip[], index: number): IcLora | undefined =>
            clips[clipIdx]?.icLoras[index];
        const hdrDecision = clipCapabilities.decision("hdr");

        {
            const fields = col;

            const persistedHdr = isHdrFeature(entry);
            const preset = findIcLoraPreset(entry.preset);
            const driveMediaKinds = entry.driveMediaKinds;
            const audioDriveMedia = entry.driveData === "audio";
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
            preserveSelectedOption(
                presetSpecs,
                entry.preset,
                "start",
                (value) => ({
                    value,
                    label: `${value} (unsupported persisted value)`,
                    disabled: true,
                }),
            );
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
                            target.lora = IC_LORA_AUTO;
                            target.strength = preset.strength;
                            target.controlType = preset.controlType;
                        }
                        const nextContract = icLoraDriveMediaContract(preset);
                        target.driveData = nextContract.driveData;
                        target.driveMediaKinds = [
                            ...nextContract.acceptedKinds,
                        ];
                        if (nextContract.driveData !== "visual") {
                            target.controlType = "none";
                        }
                        const driveData = target.driveMedia?.data ?? "";
                        if (
                            driveData &&
                            !target.driveMediaKinds.some((kind) =>
                                driveData.startsWith(`data:${kind}/`),
                            )
                        ) {
                            target.driveMedia = null;
                        }
                        const targetClip = clips[clipIdx];
                        if (
                            targetClip &&
                            target.driveSource === IC_LORA_SOURCE_INCOMING &&
                            !canUseIncomingIcLoraDrive(
                                target,
                                targetClip,
                                clipIdx,
                                clips,
                                context.generatedEntryMode(),
                            )
                        ) {
                            target.driveSource = IC_LORA_SOURCE_UPLOAD;
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
            const loraSelect = buildOptionSelect(
                loraSpecs,
                entry.lora,
                (value) => {
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
                },
            );
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

            if (entry.driveData === "visual") {
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
            }

            if (
                entry.driveData === "visual" &&
                (!preset || (preset.allowedControlTypes?.length ?? 0) > 1)
            ) {
                const allowedControlTypes =
                    preset?.allowedControlTypes ??
                    (["none", "canny", "depth", "normal"] as const);
                const controlSelect = buildOptionSelect(
                    [
                        { value: "none", label: "None (raw video)" },
                        { value: "canny", label: "Canny edges" },
                        { value: "depth", label: "Depth map" },
                        { value: "normal", label: "Normal map" },
                    ].filter((option) =>
                        allowedControlTypes.includes(
                            option.value as IcLoraControlType,
                        ),
                    ),
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
                        "Preprocesses visual drive media into a control signal before " +
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
                        canonicalizeIcLoraFields(target);
                        const targetClip = clips[clipIdx];
                        if (
                            targetClip &&
                            target.driveSource === IC_LORA_SOURCE_INCOMING &&
                            !canUseIncomingIcLoraDrive(
                                target,
                                targetClip,
                                clipIdx,
                                clips,
                                context.generatedEntryMode(),
                            )
                        ) {
                            target.driveSource = IC_LORA_SOURCE_UPLOAD;
                        }
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

            if (!preset) {
                const dataSelect = buildOptionSelect(
                    [
                        { value: "visual", label: "Visual frames" },
                        { value: "audio", label: "Audio" },
                        { value: "none", label: "None (model only)" },
                    ],
                    entry.driveData,
                    (value) => {
                        context.commit((clips) => {
                            const target = entryAt(clips, entryIdx);
                            if (!target) {
                                return;
                            }
                            target.driveData = value as IcLora["driveData"];
                            target.driveMediaKinds = [
                                ...icLoraDriveMediaContractForData(
                                    target.driveData,
                                ).acceptedKinds,
                            ];
                            if (target.driveData !== "visual") {
                                target.controlType = "none";
                            }
                            if (target.driveData === "none") {
                                target.driveSource = IC_LORA_SOURCE_UPLOAD;
                                target.driveMedia = null;
                                return;
                            }
                            const data = target.driveMedia?.data ?? "";
                            if (
                                data &&
                                !target.driveMediaKinds.some((kind) =>
                                    data.startsWith(`data:${kind}/`),
                                )
                            ) {
                                target.driveMedia = null;
                            }
                            const targetClip = clips[clipIdx];
                            if (
                                targetClip &&
                                target.driveSource ===
                                    IC_LORA_SOURCE_INCOMING &&
                                !canUseIncomingIcLoraDrive(
                                    target,
                                    targetClip,
                                    clipIdx,
                                    clips,
                                    context.generatedEntryMode(),
                                )
                            ) {
                                target.driveSource = IC_LORA_SOURCE_UPLOAD;
                            }
                        });
                        context.render();
                    },
                );
                fields.appendChild(
                    buildField(
                        "Drive data",
                        dataSelect,
                        undefined,
                        "Which stream this custom IC-LoRA extracts from its drive " +
                            "source. Visual frames create an IC-LoRA guide; Audio " +
                            "creates speaker/audio reference tokens; None applies " +
                            "only the model patch.",
                    ),
                );
            }

            if (entry.driveData !== "none") {
                const currentClips = getClips();
                const incomingAvailable = canUseIncomingIcLoraDrive(
                    entry,
                    clip,
                    clipIdx,
                    currentClips,
                    context.generatedEntryMode(),
                );
                const sourceSelect = buildOptionSelect(
                    [
                        { value: IC_LORA_SOURCE_UPLOAD, label: "Upload" },
                        {
                            value: IC_LORA_SOURCE_INCOMING,
                            label: incomingAvailable
                                ? "Incoming media"
                                : "Incoming media (unavailable)",
                            disabled: !incomingAvailable,
                        },
                    ],
                    entry.driveSource,
                    (value) => {
                        context.commit((clips) => {
                            const target = entryAt(clips, entryIdx);
                            if (target) {
                                target.driveSource = value;
                                if (value !== IC_LORA_SOURCE_UPLOAD) {
                                    target.driveMedia = null;
                                }
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
                        "Where the selected drive data comes from: Upload your own " +
                            "media, or use compatible media already entering this " +
                            "generation point.",
                    ),
                );
            }

            if (
                entry.driveData !== "none" &&
                entry.driveSource === IC_LORA_SOURCE_UPLOAD
            ) {
                const acceptedKinds = driveMediaKinds;
                fields.appendChild(
                    buildMediaPickRow(
                        "Drive Media",
                        acceptedKinds.map((kind) => `${kind}/*`).join(","),
                        [...acceptedKinds],
                        entry.driveMedia?.fileName,
                        (data, fileName) => {
                            context.commit((clips) => {
                                const target = entryAt(clips, entryIdx);
                                if (target) {
                                    target.driveMedia = { data, fileName };
                                }
                            });
                            context.render();
                        },
                        () => {
                            context.commit((clips) => {
                                const target = entryAt(clips, entryIdx);
                                if (target) {
                                    target.driveMedia = null;
                                }
                            });
                            context.render();
                        },
                    ),
                );
                if (audioDriveMedia) {
                    const hint = document.createElement("small");
                    hint.className = "vst-detail-field-hint";
                    hint.textContent =
                        "Only this media's audio is used as the reference sample. " +
                        "For a video upload, its frames are ignored; the clip's normal " +
                        "text, image, or video entry path supplies visuals.";
                    fields.appendChild(hint);
                }
            } else if (entry.driveSource === IC_LORA_SOURCE_INCOMING) {
                const hint = document.createElement("small");
                hint.className = "vst-detail-field-hint";
                hint.textContent =
                    entry.stage >= 0
                        ? `Uses ${entry.driveData} from stage ${entry.stage}'s incoming media.`
                        : `Uses ${entry.driveData} from each stage's incoming media.`;
                fields.appendChild(hint);
            } else if (entry.driveData !== "none") {
                const slot = document.createElement("small");
                slot.className = "vst-detail-field-hint";
                slot.textContent = `Driven by ${entry.driveSource} (legacy source)`;
                fields.appendChild(slot);
            }

            const hintText = [preset?.note ?? "", icLoraTriggerHint(preset)]
                .filter(Boolean)
                .join(" ");
            if (hintText || preset) {
                const hint = document.createElement("small");
                hint.className = "vst-detail-field-hint";
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
                ensureIcLoraAutoWeights(
                    entry,
                    defaults.loraValues,
                    context.render,
                );
            }
            const autoText = icLoraAutoHint(entry, defaults.loraValues);
            if (autoText) {
                const autoHint = document.createElement("small");
                autoHint.className = "vst-detail-field-hint";
                if (preset) {
                    autoHint.setAttribute(IC_LORA_AUTO_HINT_ATTR, preset.id);
                }
                autoHint.textContent = autoText;
                fields.appendChild(autoHint);
            }
            if (persistedHdr && !hdrDecision.supported) {
                disableCapabilityControls(fields, hdrDecision);
            }
        }

        return col;
    };

    return buildSection(buildEditor);
};
