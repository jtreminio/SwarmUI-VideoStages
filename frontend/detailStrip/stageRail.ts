import { reconcileArchitectureIncomingIcLoraDrives } from "../architectures/behaviorRegistry";
import { reconcileSourcedClipIdentity } from "../architectures/policy";
import { buildRepeatingEditor } from "../detailWidgets";
import { stageChipLabel, stageChipTitle } from "../timelineDetail";
import type { Clip } from "../types";
import type { DetailStripContext } from "./context";

export const buildStageRail = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    stageIdx: number,
    editor?: HTMLElement,
): HTMLElement => {
    const canAdd =
        clip.stages.length === 0 ||
        context.capabilities().forClip(clip).decision("multiStage").supported;
    const addTitle = canAdd
        ? clip.stages.length === 0
            ? "Add the first stage and choose its architecture"
            : "Add a refine stage"
        : context.capabilities().forClip(clip).decision("multiStage").reason;
    const cannotDelete =
        clip.stages.length === 0 ||
        (clip.stages.length === 1 && clip.sourceVideo === null);

    return buildRepeatingEditor({
        key: "stages",
        label: "Stages",
        sectionClass: "vst-detail-stage-groups",
        listClass: "vst-detail-stage-group-list",
        items: clip.stages.map((stage, index) => ({
            label: `Stage ${stageChipLabel(index)}`,
            focusKey: `stage-group-${index}`,
            title: stageChipTitle(stage, index),
            active: index === stageIdx,
            className: `vst-stage-tab${stage.skipped ? " vst-stage-tab-skipped" : ""}`,
            onSelect: () => context.selectStage(clipIdx, index),
            onDelete: () => context.deleteStage(clipIdx, index),
            deleteDisabled: cannotDelete,
            deleteTitle: cannotDelete
                ? clip.stages.length === 0
                    ? "This source-only clip has no generation stage"
                    : "Add a source video before removing the only generation stage"
                : `Delete stage ${stageChipLabel(index)}`,
            headerAction: {
                label: stage.skipped ? "Enable" : "Skip",
                title: stage.skipped
                    ? `Enable stage ${stageChipLabel(index)}`
                    : `Skip stage ${stageChipLabel(index)}`,
                className: "vst-detail-skip-stage",
                active: stage.skipped,
                onClick: () => {
                    context.commit((clips) => {
                        const targetClip = clips[clipIdx];
                        const target = targetClip?.stages[index];
                        if (!targetClip || !target) {
                            return;
                        }
                        target.skipped = !target.skipped;
                        reconcileSourcedClipIdentity(
                            targetClip,
                            context.capabilities().catalog,
                        );
                        reconcileArchitectureIncomingIcLoraDrives(
                            clips,
                            context.generatedEntryMode(),
                        );
                    });
                    context.render();
                },
            },
        })),
        add: {
            title: addTitle,
            className: "vst-detail-add-stage",
            disabled: !canAdd,
            onClick: () => context.addStage(clipIdx),
        },
        remove: {
            title: "Delete stage",
            className: "vst-detail-delete-stage",
        },
        editor,
    }).section;
};
