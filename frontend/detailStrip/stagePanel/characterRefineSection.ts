import { clipReferenceIsUsable } from "../../clipReferenceSource";
import {
    appendHelp,
    buildAccordionSection,
    buildCheckbox,
    buildField,
    buildOptionSelect,
} from "../../detailWidgets";
import {
    MEDIA_SOURCE_PREVIOUS_CLIP,
    MEDIA_SOURCE_UPLOAD,
} from "../../generatedMediaSource";
import {
    MINIMAX_CHARACTER_REFINE_CROP_DIMENSION_MAX,
    MINIMAX_CHARACTER_REFINE_CROP_DIMENSION_MIN,
    MINIMAX_CHARACTER_REFINE_CROP_DIMENSION_STEP,
    MINIMAX_CHARACTER_REFINE_DETECTION_THRESHOLD_MAX,
    MINIMAX_CHARACTER_REFINE_DETECTION_THRESHOLD_MIN,
    MINIMAX_CHARACTER_REFINE_DETECTION_THRESHOLD_STEP,
    MINIMAX_CHARACTER_REFINE_FEATURE,
    MINIMAX_CHARACTER_REFINE_PART_DEFAULT,
    MINIMAX_CHARACTER_REFINE_SUBJECT_DEFAULT,
} from "../../generatedMiniMaxCharacterRefine";
import { getVideoStagesHostBridge } from "../../host";
import { defaultCharacterRefine } from "../../normalizationStage";
import type { CharacterRefine, ClipReference } from "../../types";
import type { StagePanelBindings } from "./types";

const referenceLabel = (reference: ClipReference): string =>
    reference.uploadedMedia?.fileName?.trim() ||
    reference.source.trim() ||
    "Image reference";

const textInput = (
    bindings: StagePanelBindings,
    key: "subject" | "part",
    fallback: string,
): HTMLInputElement => {
    const input = document.createElement("input");
    input.type = "text";
    input.className = "auto-text";
    input.maxLength = 120;
    input.placeholder = fallback;
    input.value = bindings.stage.characterRefine?.[key] ?? fallback;
    input.addEventListener("change", () => {
        const value = input.value.trim() || fallback;
        input.value = value;
        bindings.commit((stage) => {
            if (stage.characterRefine) stage.characterRefine[key] = value;
        });
    });
    return input;
};

const pictureRows = (
    bindings: StagePanelBindings,
    imageReferences: ClipReference[],
): HTMLElement => {
    const rows = document.createElement("div");
    rows.className = "vst-detail-span-full vst-character-refine-pictures";
    const title = document.createElement("div");
    title.className = "vst-character-refine-pictures-title";
    title.setAttribute("role", "heading");
    title.setAttribute("aria-level", "4");
    title.textContent = "Reference Pictures";
    rows.appendChild(title);
    imageReferences.forEach((reference, index) => {
        if (!reference.id) return;
        const row = document.createElement("label");
        row.className = "vst-character-refine-picture";
        row.title =
            "Use this picture to guide how the person should look after the repair.";
        const checkbox = document.createElement("input");
        checkbox.type = "checkbox";
        checkbox.className = "auto-checkbox";
        checkbox.checked =
            bindings.stage.characterRefine?.pictureReferenceIds.includes(
                reference.id,
            ) ?? false;
        checkbox.addEventListener("change", () =>
            bindings.commit((stage) => {
                if (!stage.characterRefine || !reference.id) return;
                const selected = new Set(
                    stage.characterRefine.pictureReferenceIds,
                );
                if (checkbox.checked) selected.add(reference.id);
                else selected.delete(reference.id);
                stage.characterRefine.pictureReferenceIds = imageReferences
                    .map((candidate) => candidate.id)
                    .filter((id): id is string =>
                        Boolean(id && selected.has(id)),
                    );
            }),
        );
        const text = document.createElement("span");
        text.textContent = `<picture ${index + 1}> ${referenceLabel(reference)}`;
        row.append(checkbox, text);
        rows.appendChild(row);
    });
    return rows;
};

export const appendCharacterRefineSection = (
    bindings: StagePanelBindings,
): void => {
    const {
        context,
        clip,
        clipIdx,
        stage,
        stageIdx,
        fields: stageFields,
        commit,
        slider,
    } = bindings;
    const sourceVideo = clip.initVideo;
    const sourceVideoKind = sourceVideo?.source ?? MEDIA_SOURCE_UPLOAD;
    const hasDecodedVideo =
        stageIdx > 0 ||
        (sourceVideo !== null &&
            (sourceVideoKind === MEDIA_SOURCE_UPLOAD ||
                sourceVideoKind === MEDIA_SOURCE_PREVIOUS_CLIP));
    if (
        context.authoring().capabilities.forClip(clip).architectureId !==
            "minimax" ||
        !getVideoStagesHostBridge().hasBackendFeature(
            MINIMAX_CHARACTER_REFINE_FEATURE,
        ) ||
        !hasDecodedVideo
    ) {
        return;
    }
    const imageReferences = clip.references.filter(
        (reference) =>
            reference.kind === "image" &&
            reference.id &&
            clipReferenceIsUsable(reference),
    );
    const fields = document.createElement("div");
    fields.className = "vst-detail-fields vst-character-refine-fields";
    const appendGroup = (): void => {
        const enabled = stage.characterRefine !== null;
        const built = buildAccordionSection({
            key: `bonita-${clip.id ?? clipIdx}-${stage.id ?? stageIdx}`,
            label: "Make Them Bonita",
            content: fields,
            defaultOpen: enabled,
            flattenContent: true,
            className: "vst-character-refine-section vst-detail-span-full",
            toggle: {
                checked: enabled,
                title: enabled
                    ? "Turn off Make Them Bonita"
                    : "Turn on Make Them Bonita",
                onChange: (checked) => {
                    commit((target) => {
                        target.characterRefine = checked
                            ? defaultCharacterRefine(
                                  imageReferences.flatMap((reference) =>
                                      reference.id ? [reference.id] : [],
                                  ),
                              )
                            : null;
                        if (checked) target.upscale = 1;
                    });
                    context.render();
                },
            },
        });
        built.section.hidden = stage.upscale !== 1;
        appendHelp(
            built.heading,
            built.section,
            "Make Them Bonita",
            "Repair a person or character in this stage.",
        );
        stageFields.appendChild(built.section);
    };
    if (!stage.characterRefine) {
        appendGroup();
        return;
    }

    fields.appendChild(
        buildField(
            "Finder",
            buildOptionSelect(
                [
                    { value: "sam3", label: "SAM3" },
                    { value: "yolo", label: "YOLO" },
                ],
                stage.characterRefine.finder,
                (value) =>
                    commit((target) => {
                        if (target.characterRefine) {
                            target.characterRefine.finder =
                                value as CharacterRefine["finder"];
                        }
                    }),
            ),
            undefined,
            "SAM3 is usually best for movement. YOLO is faster for simple shots.",
        ),
    );
    fields.appendChild(
        buildField(
            "Subject",
            textInput(
                bindings,
                "subject",
                MINIMAX_CHARACTER_REFINE_SUBJECT_DEFAULT,
            ),
            undefined,
            'Describe who or what to follow, such as "person" or "dog".',
        ),
    );
    fields.appendChild(
        buildField(
            "Part",
            textInput(bindings, "part", MINIMAX_CHARACTER_REFINE_PART_DEFAULT),
            undefined,
            'Name the area you want to repair, such as "face" or "hand".',
        ),
    );
    fields.appendChild(
        slider(
            "Subject Confidence",
            "character-refine-subject-confidence",
            stage.characterRefine.subjectConfidence,
            MINIMAX_CHARACTER_REFINE_DETECTION_THRESHOLD_MIN,
            MINIMAX_CHARACTER_REFINE_DETECTION_THRESHOLD_MAX,
            MINIMAX_CHARACTER_REFINE_DETECTION_THRESHOLD_STEP,
            (target, value) => {
                if (target.characterRefine) {
                    target.characterRefine.subjectConfidence = value;
                }
            },
            {
                help: "Lower this if Bonita loses the subject. Raise it if Bonita follows the wrong one.",
            },
        ),
    );
    fields.appendChild(
        slider(
            "Part Confidence",
            "character-refine-part-confidence",
            stage.characterRefine.partConfidence,
            MINIMAX_CHARACTER_REFINE_DETECTION_THRESHOLD_MIN,
            MINIMAX_CHARACTER_REFINE_DETECTION_THRESHOLD_MAX,
            MINIMAX_CHARACTER_REFINE_DETECTION_THRESHOLD_STEP,
            (target, value) => {
                if (target.characterRefine) {
                    target.characterRefine.partConfidence = value;
                }
            },
            {
                help: "Lower this if Bonita misses the area. Raise it if Bonita chooses the wrong area.",
            },
        ),
    );
    for (const [label, key, help] of [
        [
            "Crop Width",
            "cropWidth",
            "Choose a wider repair image to include more space around the subject.",
        ],
        [
            "Crop Height",
            "cropHeight",
            "Choose a taller repair image to include more space above and below the subject.",
        ],
    ] as const) {
        fields.appendChild(
            slider(
                label,
                `character-refine-${key}`,
                stage.characterRefine[key],
                MINIMAX_CHARACTER_REFINE_CROP_DIMENSION_MIN,
                MINIMAX_CHARACTER_REFINE_CROP_DIMENSION_MAX,
                MINIMAX_CHARACTER_REFINE_CROP_DIMENSION_STEP,
                (target, value) => {
                    if (target.characterRefine) {
                        target.characterRefine[key] = value;
                    }
                },
                {
                    help,
                    sliderMin: 256,
                    sliderMax: 2048,
                    isPot: true,
                },
            ),
        );
    }
    fields.appendChild(pictureRows(bindings, imageReferences));
    fields.appendChild(
        buildCheckbox(
            "Verify Identity",
            stage.characterRefine.verifyIdentity,
            (enabled) =>
                commit((target) => {
                    if (target.characterRefine) {
                        target.characterRefine.verifyIdentity = enabled;
                    }
                }),
            {
                disabled: imageReferences.length === 0,
                help: "InsightFace adds a slower identity check. Turn it on only when the scene has more than one person.",
            },
        ),
    );
    appendGroup();
};
