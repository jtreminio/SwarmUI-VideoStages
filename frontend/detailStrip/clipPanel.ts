import { buildArchitectureIcLorasSection } from "../architectures/authoringPanels";
import { executableBoundaryForLeftClip } from "../clipSemantics";
import { buildStaticSection } from "../detailWidgets";
import { H3_ATTENTION_WINDOW_FEATURE } from "../h3AttentionWindow";
import { getVideoStagesHostBridge } from "../host";
import type { AuthoringDocument, TimelineSelection } from "../types";
import { applyPersistedCapabilityRepair } from "./capabilityUi";
import { buildClipColumn, buildClipSkipAction } from "./clipBasics";
import { buildClipLorasSection } from "./clipLorasPanel";
import { buildClipReferenceSection } from "./clipReferencePanel";
import type { DetailStripContext } from "./context";
import { buildInitVideoSection } from "./initVideoPanel";
import { buildRefSection } from "./refPanel";
import { buildRetakeSection } from "./retakePanel";
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
        {
            kind:
                | "clip"
                | "ref"
                | "clip-ref"
                | "boundary-ref"
                | "ic-lora"
                | "retake";
        }
    >,
    state: AuthoringDocument,
): HTMLElement => {
    const clips = state.clips;
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-clip-body";
    let clipIdx: number;
    if (selection.kind === "boundary-ref") {
        const incomingBoundary = executableBoundaryForLeftClip(
            clips,
            selection.leftClipIdx,
        );
        if (!incomingBoundary) {
            throw new Error(
                "boundary reference selection has no executable seam",
            );
        }
        clipIdx = incomingBoundary.rightIdx;
    } else {
        clipIdx = selection.clipIdx;
    }
    const stageIdx = selection.kind === "clip" ? selection.stageIdx : 0;
    const clip = clips[clipIdx];
    body.classList.toggle("vst-detail-clip-skipped", clip.skipped === true);
    const { capabilities, defaults } = context.authoring();
    const capabilityView = capabilities.forClip(clip);
    const referenceFramingState = capabilityView.authoringState(
        "referenceFraming",
        clip.refFraming !== "crop",
    );

    body.appendChild(
        buildStaticSection({
            key: "clip",
            label: "Clip",
            className: "vst-detail-clip-section",
            content: buildClipColumn(
                context,
                clip,
                clipIdx,
                referenceFramingState,
                capabilityView.architectureId === "minimax" &&
                    getVideoStagesHostBridge().hasBackendFeature(
                        H3_ATTENTION_WINDOW_FEATURE,
                    ),
            ),
            flattenContent: true,
            headerActions:
                clipIdx === 0
                    ? []
                    : [
                          buildClipSkipAction(context, clip, clipIdx),
                          {
                              label: "×",
                              title: `Delete clip ${clipIdx}`,
                              className:
                                  "vst-detail-delete vst-detail-repeating-group-delete vst-detail-delete-clip",
                              variant: "interrupt",
                              onClick: () => context.deleteClip?.(clipIdx),
                          },
                      ],
        }).section,
    );
    const stages = buildStageRail(
        context,
        clip,
        clipIdx,
        stageIdx,
        (editorStageIdx) => {
            const editorStage = clip.stages[editorStageIdx];
            return editorStage
                ? buildStageParamsColumn(
                      context,
                      clip,
                      clipIdx,
                      editorStageIdx,
                      editorStage,
                      defaults,
                  )
                : undefined;
        },
        selection.kind === "clip",
    );
    if (!clip.stages[stageIdx]) {
        const note = document.createElement("p");
        note.className = "vst-detail-note vst-source-only-note";
        note.textContent =
            "Source-only clip. Add a stage to choose an architecture and refine this footage.";
        stages.appendChild(note);
    }
    body.appendChild(stages);
    const appendCapabilitySection = (
        feature: "clipReferences" | "frameReferences" | "icLora" | "retake",
        persisted: boolean,
        content: () => HTMLElement | DocumentFragment,
    ): void => {
        const state = capabilityView.authoringState(feature, persisted);
        if (!state.visible) {
            return;
        }
        const section = content();
        if (!state.enabled) {
            // Read-only, but the section's own delete stays operable so the
            // persisted value can always be removed.
            applyPersistedCapabilityRepair(section, state);
        }
        body.appendChild(section);
    };
    appendCapabilitySection("clipReferences", clip.references.length > 0, () =>
        buildClipReferenceSection(
            context,
            clipIdx,
            selection.kind === "clip-ref" ? selection.referenceIdx : null,
            clips,
            state.fps,
            selection.kind === "clip-ref" || selection.kind === "boundary-ref",
            selection.kind === "boundary-ref",
        ),
    );
    appendCapabilitySection("frameReferences", clip.frameRefs.length > 0, () =>
        buildRefSection(
            context,
            clipIdx,
            selection.kind === "ref" ? selection.refIdx : null,
            clips,
            state.fps,
            selection.kind === "ref",
        ),
    );
    body.appendChild(
        buildClipLorasSection(context, clip, clipIdx, stageIdx, defaults),
    );
    appendCapabilitySection("icLora", clip.icLoras.length > 0, () =>
        buildArchitectureIcLorasSection(
            context,
            clip,
            clipIdx,
            defaults,
            selection.kind === "ic-lora" ? selection.entryIdx : null,
            selection.kind === "ic-lora",
        ),
    );
    body.appendChild(buildInitVideoSection(context, clip, clipIdx, false));
    appendCapabilitySection("retake", clip.retake !== null, () =>
        buildRetakeSection(context, clip, clipIdx, selection.kind === "retake"),
    );
    return body;
};
