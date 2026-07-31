import { buildSlider, tagFocus } from "../detailWidgets";
import type { Clip, RootDefaults, Stage } from "../types";
import type { DetailStripContext } from "./context";
import { appendStageModelSection } from "./stagePanel/modelSection";
import { appendStageReferenceGuideSection } from "./stagePanel/referenceGuideSection";
import {
    appendStageDenoisingSection,
    appendStageSamplerSchedulerSection,
} from "./stagePanel/samplingSection";
import type { StagePanelBindings } from "./stagePanel/types";
import { appendStageUpscaleSection } from "./stagePanel/upscaleSection";

export const buildStageParamsColumn = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    stageIdx: number,
    stage: Stage,
    defaults: RootDefaults,
): HTMLElement => {
    const column = document.createElement("div");
    column.className = "vst-detail-fields vst-detail-params";
    const sourcedStage0 =
        stageIdx === 0 && !!clip.sourceVideo && stage.skipped !== true;
    const isRefine = stageIdx >= 1 || sourcedStage0;
    const stageCapabilities = context
        .authoring()
        .capabilities.forStage(clip, stage);
    if (clip.loras.length > 0) {
        column.dataset.vstStageLorasSupported = `${
            stageCapabilities.decision("stageLoras").supported
        }`;
    }

    const commit = (mutate: (target: Stage) => void): void => {
        context.commit((clips) => {
            const target = clips[clipIdx]?.stages[stageIdx];
            if (target) mutate(target);
        });
    };
    const debouncedCommit = (
        key: string,
        mutate: (target: Stage) => void,
    ): void => {
        context.debouncedCommit(key, (clips) => {
            const target = clips[clipIdx]?.stages[stageIdx];
            if (target) mutate(target);
        });
    };
    const slider: StagePanelBindings["slider"] = (
        label,
        focusKey,
        value,
        min,
        max,
        step,
        assign,
        options,
    ) =>
        tagFocus(
            buildSlider(
                label,
                value,
                min,
                max,
                step,
                (nextValue) => {
                    options?.onValue?.(nextValue);
                    debouncedCommit(focusKey, (target) =>
                        assign(target, nextValue),
                    );
                },
                options?.title || options?.help
                    ? { title: options.title, help: options.help }
                    : undefined,
            ),
            focusKey,
        );
    const fields = column;
    fields.classList.toggle("vst-stage-fields-muted", stage.skipped === true);

    const bindings: StagePanelBindings = {
        context,
        clip,
        clipIdx,
        stageIdx,
        stage,
        defaults,
        fields,
        stageCapabilities,
        commit,
        debouncedCommit,
        slider,
    };
    appendStageModelSection(bindings);
    appendStageDenoisingSection(bindings, isRefine);
    appendStageUpscaleSection(bindings, isRefine);
    appendStageSamplerSchedulerSection(bindings);
    appendStageReferenceGuideSection(bindings);

    if (sourcedStage0) {
        const note = document.createElement("p");
        note.className = "vst-detail-note vst-stage-passthrough-note";
        note.textContent =
            "This stage starts from the source footage — Control sets how much is re-generated (0 passes it through).";
        column.insertBefore(note, column.firstChild);
    }
    return column;
};
