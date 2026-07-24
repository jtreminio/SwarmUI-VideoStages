import {
    architectureCatalogView,
    architectureDescriptor,
    buildArchitectureRetargetPlan,
} from "../../architectures/catalog";
import { architectureSupportsClipStart } from "../../architectures/conversion/entryModePolicy";
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
    const modelView =
        stageIdx === 0
            ? {
                  values: defaults.modelCatalog.entries.flatMap((entry) => {
                      const architecture = architectureDescriptor(
                          defaults.modelCatalog,
                          entry.architectureId,
                      );
                      return architecture &&
                          architectureSupportsClipStart(
                              architecture.capabilities,
                              clip,
                              context.generatedEntryMode(),
                          )
                          ? [entry.value]
                          : [];
                  }),
                  labels: defaults.modelCatalog.entries.flatMap(
                      (entry): string[] => {
                          const architecture = architectureDescriptor(
                              defaults.modelCatalog,
                              entry.architectureId,
                          );
                          return architecture &&
                              architectureSupportsClipStart(
                                  architecture.capabilities,
                                  clip,
                                  context.generatedEntryMode(),
                              )
                              ? [entry.label]
                              : [];
                      },
                  ),
              }
            : architectureCatalogView(defaults.modelCatalog, clip.architecture);
    const modelOptions: OptionSpec[] = modelView.values.map((value, index) => ({
        value,
        label: modelView.labels[index] ?? value,
    }));
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
            const plan = buildArchitectureRetargetPlan(
                defaults.modelCatalog,
                value,
            );
            if (!plan) {
                modelSelect.value = stage.model;
                return;
            }
            if (stageIdx === 0 && plan.architectureId !== clip.architecture) {
                const conversion = planArchitectureConversion(
                    clip,
                    plan,
                    defaults.modelCatalog,
                );
                if (!conversion) {
                    modelSelect.value = stage.model;
                    return;
                }
                const fromLabel =
                    architectureDescriptor(
                        defaults.modelCatalog,
                        clip.architecture,
                    )?.label ?? clip.architecture;
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
