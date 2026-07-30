import {
    architectureDescriptor,
    buildArchitectureRetargetPlan,
    modelCatalogEntry,
} from "../../architectures/catalog";
import { modelSupportsStageEntry } from "../../architectures/conversion/entryModePolicy";
import { planArchitectureConversion } from "../../architectures/conversion/plan";
import {
    architectureConversionMessage,
    confirmArchitectureConversion,
} from "../../architectures/conversion/presentation";
import {
    buildField,
    buildOptionSelect,
    type OptionSpec,
} from "../../detailWidgets";
import { dispatchDocumentCommand, getTimelineStore } from "../../persistence";
import type { StagePanelBindings } from "./types";

export const appendStageModelSection = ({
    context,
    clip,
    clipIdx,
    stageIdx,
    stage,
    defaults,
    fields,
}: StagePanelBindings): void => {
    const rootModel = modelCatalogEntry(
        defaults.modelCatalog,
        clip.stages[0]?.model,
    );
    // Stage 0 owns authored architecture. The cached clip field is only a
    // repair fallback when a persisted Stage 0 model no longer resolves.
    const ownerArchitectureId = rootModel?.architectureId ?? clip.architecture;
    const modelOptions: OptionSpec[] = defaults.modelCatalog.entries.flatMap(
        (entry): OptionSpec[] => {
            const model = modelCatalogEntry(defaults.modelCatalog, entry.value);
            const target = buildArchitectureRetargetPlan(
                defaults.modelCatalog,
                entry.value,
            );
            if (!target || !model) return [];
            const leavesAuthoredStagesCompatible = clip.stages.every(
                (candidate, candidateIndex) => {
                    if (candidateIndex === stageIdx) return true;
                    const candidateModel = modelCatalogEntry(
                        defaults.modelCatalog,
                        candidate.model,
                    );
                    return (
                        candidateModel?.architectureId ===
                            model.architectureId &&
                        candidateModel.compatibilityClassId !== null &&
                        candidateModel.compatibilityClassId ===
                            model.compatibilityClassId
                    );
                },
            );
            const preservesClipLock =
                stageIdx === 0
                    ? target.architectureId !== ownerArchitectureId ||
                      leavesAuthoredStagesCompatible
                    : model.architectureId === rootModel?.architectureId &&
                      model.compatibilityClassId !== null &&
                      model.compatibilityClassId ===
                          rootModel?.compatibilityClassId;
            const supportsRetargetedRoles =
                stageIdx === 0 && target.architectureId !== ownerArchitectureId
                    ? planArchitectureConversion(
                          clip,
                          target,
                          defaults.modelCatalog,
                          context.generatedEntryMode(),
                      ) !== null
                    : modelSupportsStageEntry(
                          model,
                          clip,
                          stageIdx,
                          context.generatedEntryMode(),
                      );
            return preservesClipLock && supportsRetargetedRoles
                ? [{ value: entry.value, label: entry.label }]
                : [];
        },
    );
    if (
        stage.model &&
        !modelOptions.some((option) => option.value === stage.model)
    ) {
        modelOptions.unshift({
            value: stage.model,
            label: `${stage.model} (unsupported persisted value)`,
            disabled: true,
        });
    }
    const modelSelect = buildOptionSelect(
        modelOptions,
        `${stage.model ?? ""}`,
        (value) => {
            const selectedModel = modelCatalogEntry(
                defaults.modelCatalog,
                value,
            );
            const plan = buildArchitectureRetargetPlan(
                defaults.modelCatalog,
                value,
            );
            if (!plan || !selectedModel) {
                modelSelect.value = stage.model;
                return;
            }
            if (stageIdx === 0 && plan.architectureId !== ownerArchitectureId) {
                const conversion = planArchitectureConversion(
                    clip,
                    plan,
                    defaults.modelCatalog,
                    context.generatedEntryMode(),
                );
                if (!conversion) {
                    modelSelect.value = stage.model;
                    return;
                }
                const fromLabel =
                    architectureDescriptor(
                        defaults.modelCatalog,
                        ownerArchitectureId,
                    )?.label ?? ownerArchitectureId;
                const toLabel =
                    architectureDescriptor(
                        defaults.modelCatalog,
                        plan.architectureId,
                    )?.label ?? plan.architectureId;
                const confirmedAndApplied = confirmArchitectureConversion(
                    architectureConversionMessage(
                        fromLabel,
                        toLabel,
                        conversion.removals,
                    ),
                    () => {
                        const snapshot = getTimelineStore().getSnapshot();
                        const clipId = snapshot.state.clips[clipIdx]?.id;
                        if (!clipId) return false;
                        const result = dispatchDocumentCommand(
                            {
                                type: "clip.convert-architecture",
                                clipId,
                                target: plan,
                            },
                            {
                                expectedRevision: snapshot.revision,
                                origin: "detail-strip",
                            },
                        );
                        if (result.applied) context.render();
                        return result.applied;
                    },
                );
                if (!confirmedAndApplied) modelSelect.value = stage.model;
                return;
            }
            const snapshot = getTimelineStore().getSnapshot();
            const clipId = snapshot.state.clips[clipIdx]?.id;
            const stageId = snapshot.state.clips[clipIdx]?.stages[stageIdx]?.id;
            if (!clipId || !stageId) {
                modelSelect.value = stage.model;
                return;
            }
            const result = dispatchDocumentCommand(
                {
                    type: "stage.retarget-model",
                    clipId,
                    stageId,
                    target: plan,
                },
                {
                    expectedRevision: snapshot.revision,
                    origin: "detail-strip",
                },
            );
            if (!result.applied) {
                modelSelect.value = stage.model;
                return;
            }
            context.render();
        },
    );
    const modelField = buildField("Model", modelSelect);
    modelField.classList.add("vst-detail-span-2");
    fields.appendChild(modelField);
};
