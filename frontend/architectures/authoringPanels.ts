import type { DetailStripContext } from "../detailStrip/context";
import { buildRepeatingEditor } from "../detailWidgets";
import { removeIcLoraStrengthAt } from "../normalizationStage";
import { setSelection } from "../selection";
import type { Clip, RootDefaults } from "../types";
import { buildIcLorasSection as buildLtx2IcLorasSection } from "./ltx2/icLoraPanel";
import { LTX2_ARCHITECTURE_ID } from "./ltx2/identity";

/** DOM-only architecture authoring panel slots used by the detail strip. */
interface ArchitectureAuthoringPanel {
    buildIcLorasSection(
        context: DetailStripContext,
        clip: Clip,
        clipIdx: number,
        defaults: RootDefaults,
        selectedEntryIdx?: number | null,
        open?: boolean,
    ): HTMLElement;
}

/**
 * Architecture catalog data remains backend-owned. This map only selects the
 * local DOM implementation for architecture-specific authoring behavior.
 */
const panels = new Map<string, ArchitectureAuthoringPanel>([
    [LTX2_ARCHITECTURE_ID, { buildIcLorasSection: buildLtx2IcLorasSection }],
]);

const persistedIcLoraRemovalPanel = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    selectedEntryIdx: number | null,
    open: boolean,
): HTMLElement => {
    const entryIdx =
        clip.icLoras.length === 0
            ? null
            : Math.max(
                  0,
                  Math.min(selectedEntryIdx ?? 0, clip.icLoras.length - 1),
              );
    const buildEditor = (index: number): HTMLElement | undefined => {
        const entry = clip.icLoras[index];
        if (!entry) {
            return undefined;
        }
        const content = document.createElement("div");
        content.className = "vst-detail-col vst-detail-iclora-col";
        const note = document.createElement("p");
        note.className = "vst-detail-note";
        note.textContent =
            "This architecture has no IC-LoRA editor. Existing entries remain available for removal.";
        content.appendChild(note);
        const label = document.createElement("span");
        label.textContent = entry.lora || `IC-LoRA ${index}`;
        content.appendChild(label);
        return content;
    };
    return buildRepeatingEditor({
        key: "ic-loras",
        label: "Persisted IC-LoRAs",
        sectionClass: "vst-detail-iclora-section",
        open,
        items: clip.icLoras.map((_, index) => ({
            label: `IC${index}`,
            focusKey: `ic-lora-tab-${index}`,
            title: `Inspect persisted IC-LoRA ${index}`,
            active: index === entryIdx,
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
                        if (!target?.icLoras[index]) return null;
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
            title: "This architecture has no IC-LoRA editor",
            label: "+ Add IC-LoRA",
            className: "vst-detail-add-iclora",
            disabled: true,
            onClick: () => {},
        },
        remove: {
            title:
                entryIdx === null
                    ? "No IC-LoRA to delete"
                    : `Delete persisted IC-LoRA ${entryIdx}`,
            className: "vst-detail-delete-iclora",
        },
        editorForItem: buildEditor,
    }).section;
};

export const buildArchitectureIcLorasSection = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    defaults: RootDefaults,
    selectedEntryIdx: number | null = null,
    open = selectedEntryIdx !== null,
): HTMLElement => {
    const architectureId = context
        .authoring()
        .capabilities.forClip(clip).architectureId;
    return (
        panels
            .get(architectureId)
            ?.buildIcLorasSection(
                context,
                clip,
                clipIdx,
                defaults,
                selectedEntryIdx,
                open,
            ) ??
        persistedIcLoraRemovalPanel(
            context,
            clip,
            clipIdx,
            selectedEntryIdx,
            open,
        )
    );
};
