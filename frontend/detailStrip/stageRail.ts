import { buildRepeatingEditor } from "../detailWidgets";
import { skipGlyph, skipTitle } from "../skipVocabulary";
import { stageChipLabel, stageChipTitle } from "../timelineDetail";
import type { Clip } from "../types";
import type { DetailStripContext } from "./context";

export const buildStageRail = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    stageIdx: number,
    editorForStage?: (stageIdx: number) => HTMLElement | undefined,
    open = true,
): HTMLElement => {
    const addTitle =
        clip.stages.length === 0
            ? "Add the first stage and choose its architecture"
            : "Add a refine stage";
    return buildRepeatingEditor({
        key: "stages",
        label: "Stages",
        sectionClass: "vst-detail-stage-groups",
        open,
        items: clip.stages.map((stage, index) => {
            const firstStage = index === 0;
            return {
                label: `Stage ${stageChipLabel(index)}`,
                focusKey: `stage-group-${index}`,
                title: stageChipTitle(stage, index),
                active: index === stageIdx,
                className: `vst-stage-tab${stage.skipped ? " vst-stage-tab-skipped" : ""}`,
                onSelect: () => context.selectStage(clipIdx, index),
                onDelete: firstStage
                    ? undefined
                    : () => context.deleteStage(clipIdx, index),
                deleteTitle: firstStage
                    ? undefined
                    : `Delete stage ${stageChipLabel(index)}`,
                headerAction: firstStage
                    ? undefined
                    : {
                          label: skipGlyph(stage.skipped === true),
                          title: skipTitle(
                              `stage ${stageChipLabel(index)}`,
                              stage.skipped === true,
                          ),
                          className: "vst-detail-skip-stage",
                          active: stage.skipped,
                          onClick: () =>
                              context.toggleStageSkip(clipIdx, index),
                      },
            };
        }),
        editorForItem: editorForStage,
        add: {
            title: addTitle,
            label: "+ Add Video Stage",
            className: "vst-detail-add-stage",
            onClick: () => context.addStage(clipIdx),
        },
        remove: {
            title: "Delete stage",
            className: "vst-detail-delete-stage",
        },
    }).section;
};
