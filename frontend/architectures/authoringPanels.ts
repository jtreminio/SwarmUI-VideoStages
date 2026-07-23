import type { DetailStripContext } from "../detailStrip/context";
import type { Clip, RootDefaults } from "../types";
import { buildIcLorasSection as buildLtx2IcLorasSection } from "./ltx2/icLoraPanel";

/** DOM-only architecture authoring panel slots used by the detail strip. */
interface ArchitectureAuthoringPanel {
    buildIcLorasSection(
        context: DetailStripContext,
        clip: Clip,
        clipIdx: number,
        defaults: RootDefaults,
    ): HTMLElement;
}

const panels = new Map<string, ArchitectureAuthoringPanel>([
    ["ltx2", { buildIcLorasSection: buildLtx2IcLorasSection }],
]);

const persistedIcLoraRemovalPanel = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
): HTMLElement => {
    const section = document.createElement("section");
    section.className = "vst-detail-col vst-detail-iclora-col";
    const heading = document.createElement("div");
    heading.className = "vst-detail-sec";
    heading.textContent = "Persisted IC-LoRAs";
    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent =
        "This architecture has no IC-LoRA editor. Existing entries remain available for removal.";
    section.append(heading, note);
    clip.icLoras.forEach((entry, entryIdx) => {
        const row = document.createElement("div");
        row.className = "vst-detail-instance vst-detail-iclora";
        const label = document.createElement("span");
        label.textContent = entry.lora || `IC-LoRA ${entryIdx + 1}`;
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "basic-button small-button vst-detail-delete";
        remove.textContent = "Remove";
        remove.addEventListener("click", () => {
            context.structuralCommit((clips) => {
                const target = clips[clipIdx];
                if (!target?.icLoras[entryIdx]) return null;
                target.icLoras.splice(entryIdx, 1);
                return "render";
            });
        });
        row.append(label, remove);
        section.appendChild(row);
    });
    return section;
};

export const buildArchitectureIcLorasSection = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    defaults: RootDefaults,
): HTMLElement =>
    panels
        .get(clip.architecture)
        ?.buildIcLorasSection(context, clip, clipIdx, defaults) ??
    persistedIcLoraRemovalPanel(context, clip, clipIdx);
