import type { DetailStripContext } from "../detailStrip/context";
import type { Clip, IcLora, RootDefaults } from "../types";
import { buildIcLoraFields as buildLtx2IcLoraFields } from "./ltx2/icLoraPanel";
import { LTX2_ARCHITECTURE_ID } from "./ltx2/identity";

const buildPersistedIcLoraFields = (
    entry: IcLora,
    index: number,
): HTMLElement => {
    const content = document.createElement("div");
    content.className =
        "vst-detail-fields vst-detail-col vst-detail-iclora-col";
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

export const buildArchitectureIcLoraFields = (
    context: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    defaults: RootDefaults,
    entryIdx: number,
): HTMLElement => {
    const entry = clip.icLoras[entryIdx];
    if (!entry) {
        throw new Error(`IC-LoRA ${entryIdx} does not exist`);
    }
    const architectureId = context
        .authoring()
        .capabilities.forClip(clip).architectureId;
    return architectureId === LTX2_ARCHITECTURE_ID
        ? buildLtx2IcLoraFields(context, clip, clipIdx, defaults, entryIdx)
        : buildPersistedIcLoraFields(entry, entryIdx);
};
