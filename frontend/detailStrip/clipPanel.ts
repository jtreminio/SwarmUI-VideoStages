import { buildArchitectureIcLorasSection } from "../architectures/authoringPanels";
import { buildGroup, sectionLabel } from "../detailWidgets";
import { getRootDefaults } from "../rootDefaults";
import type { Clip, TimelineSelection } from "../types";
import { disableCapabilityControls } from "./capabilityUi";
import { buildClipColumn } from "./clipBasics";
import type { DetailStripContext } from "./context";
import { buildRetakeSection } from "./retakePanel";
import { buildSourceVideoSection } from "./sourceVideoPanel";
import { buildStageParamsColumn } from "./stagePanel";
import { buildStageRail } from "./stageRail";

const GROUP_STAGES = "vstdock_stages";
const GROUP_ICLORA = "vstdock_iclora";
const GROUP_RETAKE = "vstdock_retake";
const GROUP_SOURCE = "vstdock_source";

/**
 * Composes the clip detail panel from focused editors. Each editor owns one
 * authoring concern; this facade owns only their order and group containers.
 */
export const buildClipBody = (
    context: DetailStripContext,
    selection: Extract<TimelineSelection, { kind: "clip" }>,
    clips: Clip[],
): HTMLElement => {
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-clip-body";
    const clip = clips[selection.clipIdx];
    const stage = clip.stages[selection.stageIdx];
    const defaults = getRootDefaults();
    const capabilityView = context.capabilities().forClip(clip);

    body.appendChild(buildClipColumn(context, clip, selection.clipIdx));
    const stages = document.createElement("div");
    stages.className = "vst-detail-stages-wrap";
    stages.append(
        sectionLabel("Stages"),
        buildStageRail(context, clip, selection.clipIdx, selection.stageIdx),
    );
    if (stage) {
        stages.appendChild(
            buildStageParamsColumn(
                context,
                clip,
                selection.clipIdx,
                selection.stageIdx,
                stage,
                defaults,
            ),
        );
    } else {
        const note = document.createElement("p");
        note.className = "vst-detail-note vst-source-only-note";
        note.textContent =
            "Source-only clip. Add a stage to choose an architecture and refine this footage.";
        stages.appendChild(note);
    }
    body.appendChild(buildGroup(GROUP_STAGES, stages));
    const appendCapabilityGroup = (
        groupId: string,
        feature: "icLora" | "sourceVideo" | "retake",
        persisted: boolean,
        content: () => HTMLElement,
        removableSelectors: readonly string[],
    ): void => {
        const state = capabilityView.authoringState(feature, persisted);
        if (!state.visible) {
            return;
        }
        const section = content();
        if (!state.enabled) {
            disableCapabilityControls(section, state, removableSelectors);
        }
        body.appendChild(buildGroup(groupId, section));
    };
    appendCapabilityGroup(
        GROUP_ICLORA,
        "icLora",
        clip.icLoras.length > 0,
        () =>
            buildArchitectureIcLorasSection(
                context,
                clip,
                selection.clipIdx,
                defaults,
            ),
        [".vst-detail-delete"],
    );
    appendCapabilityGroup(
        GROUP_SOURCE,
        "sourceVideo",
        clip.sourceVideo !== null,
        () => buildSourceVideoSection(context, clip, selection.clipIdx),
        [".vst-detail-delete"],
    );
    appendCapabilityGroup(
        GROUP_RETAKE,
        "retake",
        clip.retake !== null,
        () => buildRetakeSection(context, clip, selection.clipIdx),
        [".vst-detail-delete"],
    );
    return body;
};
