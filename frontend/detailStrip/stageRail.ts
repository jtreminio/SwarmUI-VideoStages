import { buildAddButton } from "../detailWidgets";
import { stageChipLabel, stageChipTitle } from "../timelineDetail";
import type { Clip } from "../types";
import type { DetailStripContext } from "./context";

export const buildStageRail = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    stageIdx: number,
): HTMLElement => {
    const column = document.createElement("div");
    column.className = "vst-detail-col vst-detail-rail";
    const list = document.createElement("div");
    list.className = "vst-detail-rail-list";
    clip.stages.forEach((stage, index) => {
        const chip = document.createElement("button");
        chip.type = "button";
        chip.className = "vst-chip vst-stage-tab";
        if (index === stageIdx) {
            chip.classList.add("vst-stage-tab-active");
        }
        if (stage.skipped) {
            chip.classList.add("vst-stage-tab-skipped");
        }
        chip.textContent = stageChipLabel(index);
        chip.title = `${stageChipTitle(stage, index)} · click to edit · Shift+click to delete`;
        chip.addEventListener("click", (event) => {
            if (event.shiftKey) {
                context.deleteStage(clipIdx, index);
            } else {
                context.selectStage(clipIdx, index);
            }
        });
        list.appendChild(chip);
    });
    column.appendChild(list);

    const actions = document.createElement("div");
    actions.className = "vst-detail-rail-actions";
    const addButton = buildAddButton("Add stage", "vst-detail-add-stage", () =>
        context.addStage(clipIdx),
    );
    const canAdd =
        clip.stages.length === 0 ||
        context.capabilities().forClip(clip).decision("multiStage").supported;
    addButton.disabled = !canAdd;
    addButton.title = canAdd
        ? clip.stages.length === 0
            ? "Add the first stage and choose its architecture"
            : "Add a refine stage"
        : context.capabilities().forClip(clip).decision("multiStage").reason;
    const deleteButton = document.createElement("button");
    deleteButton.type = "button";
    deleteButton.className =
        "basic-button small-button vst-refs-delete vst-detail-rail-btn vst-detail-delete-stage";
    deleteButton.textContent = "Delete stage";
    deleteButton.disabled =
        clip.stages.length === 0 ||
        (clip.stages.length === 1 && clip.sourceVideo === null);
    deleteButton.title = deleteButton.disabled
        ? clip.stages.length === 0
            ? "This source-only clip has no generation stage"
            : "Add a source video before removing the only generation stage"
        : `Delete stage ${stageChipLabel(stageIdx)}`;
    deleteButton.addEventListener("click", (event) => {
        event.preventDefault();
        context.deleteStage(clipIdx, stageIdx);
    });
    actions.append(addButton, deleteButton);
    column.appendChild(actions);
    return column;
};
