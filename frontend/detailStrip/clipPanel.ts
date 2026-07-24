import { buildArchitectureIcLorasSection } from "../architectures/authoringPanels";
import { buildStaticSection } from "../detailWidgets";
import { getRootDefaults } from "../rootDefaults";
import type { Clip, TimelineSelection } from "../types";
import { disableCapabilityControls } from "./capabilityUi";
import { buildClipColumn, buildClipSkipAction } from "./clipBasics";
import type { DetailStripContext } from "./context";
import { buildRefSection } from "./refPanel";
import { buildRetakeSection } from "./retakePanel";
import { buildSourceVideoSection } from "./sourceVideoPanel";
import { buildStageParamsColumn } from "./stagePanel";
import { buildStageRail } from "./stageRail";

/**
 * Composes the clip detail panel from focused editors. Each editor owns one
 * authoring concern; this facade owns only their order and group containers.
 */
export const buildClipBody = (
    context: DetailStripContext,
    selection: Extract<
        TimelineSelection,
        { kind: "clip" | "ref" | "ic-lora" | "retake" }
    >,
    clips: Clip[],
): HTMLElement => {
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-clip-body";
    const { clipIdx } = selection;
    const stageIdx = selection.kind === "clip" ? selection.stageIdx : 0;
    const clip = clips[clipIdx];
    body.classList.toggle("vst-detail-clip-skipped", clip.skipped === true);
    const stage = clip.stages[stageIdx];
    const defaults = getRootDefaults();
    const capabilityView = context.capabilities().forClip(clip);

    body.appendChild(
        buildStaticSection({
            key: "clip",
            label: "Clip",
            className: "vst-detail-clip-section",
            content: buildClipColumn(context, clip, clipIdx),
            flattenContent: true,
            headerAction: buildClipSkipAction(context, clip, clipIdx),
        }).section,
    );
    let stageEditor: HTMLElement | undefined;
    if (stage) {
        stageEditor = buildStageParamsColumn(
            context,
            clip,
            clipIdx,
            stageIdx,
            stage,
            defaults,
        );
    }
    const stages = buildStageRail(
        context,
        clip,
        clipIdx,
        stageIdx,
        stageEditor,
        selection.kind === "clip",
    );
    if (!stage) {
        const note = document.createElement("p");
        note.className = "vst-detail-note vst-source-only-note";
        note.textContent =
            "Source-only clip. Add a stage to choose an architecture and refine this footage.";
        stages.appendChild(note);
    }
    body.appendChild(stages);
    const appendCapabilitySection = (
        feature: "frameReferences" | "icLora" | "sourceVideo" | "retake",
        persisted: boolean,
        content: () => HTMLElement | DocumentFragment,
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
        body.appendChild(section);
    };
    appendCapabilitySection(
        "frameReferences",
        clip.refs.length > 0,
        () =>
            buildRefSection(
                context,
                clipIdx,
                selection.kind === "ref" ? selection.refIdx : null,
                clips,
                selection.kind === "ref",
            ),
        [],
    );
    appendCapabilitySection(
        "icLora",
        clip.icLoras.length > 0,
        () =>
            buildArchitectureIcLorasSection(
                context,
                clip,
                clipIdx,
                defaults,
                selection.kind === "ic-lora" ? selection.entryIdx : null,
                selection.kind === "ic-lora",
            ),
        [".vst-detail-delete"],
    );
    appendCapabilitySection(
        "sourceVideo",
        clip.sourceVideo !== null,
        () => buildSourceVideoSection(context, clip, clipIdx, false),
        [".vst-detail-delete"],
    );
    appendCapabilitySection(
        "retake",
        clip.retake !== null,
        () =>
            buildRetakeSection(
                context,
                clip,
                clipIdx,
                selection.kind === "retake",
            ),
        [".vst-detail-delete"],
    );
    return body;
};
