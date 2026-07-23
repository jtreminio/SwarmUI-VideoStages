import { buildGroup, sectionLabel } from "../detailWidgets";
import { getRootDefaults } from "../rootDefaults";
import type { Clip, TimelineSelection } from "../types";
import { buildClipColumn } from "./clipBasics";
import type { DetailStripContext } from "./context";
import { buildIcLorasSection } from "./icLoraPanel";
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

    body.appendChild(buildClipColumn(context, clip, selection.clipIdx));
    const stages = document.createElement("div");
    stages.className = "vst-detail-stages-wrap";
    stages.append(
        sectionLabel("Stages"),
        buildStageRail(context, clip, selection.clipIdx, selection.stageIdx),
        buildStageParamsColumn(
            context,
            clip,
            selection.clipIdx,
            selection.stageIdx,
            stage,
            defaults,
        ),
    );
    body.appendChild(buildGroup(GROUP_STAGES, stages));
    body.appendChild(
        buildGroup(
            GROUP_ICLORA,
            buildIcLorasSection(context, clip, selection.clipIdx, defaults),
        ),
    );
    body.appendChild(
        buildGroup(
            GROUP_SOURCE,
            buildSourceVideoSection(context, clip, selection.clipIdx),
        ),
    );
    body.appendChild(
        buildGroup(
            GROUP_RETAKE,
            buildRetakeSection(context, clip, selection.clipIdx),
        ),
    );
    return body;
};
