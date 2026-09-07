import { buildArchitectureIcLoraFields } from "../architectures/authoringPanels";
import {
    appendDefaultIcLora,
    removeIcLoraEntryAt,
} from "../architectures/ltx2/icLoraEntries";
import type { CapabilityDecision } from "../architectures/policy/types";
import { referenceEndpointPolicy } from "../architectures/referenceEndpoints";
import { defaultAuthoringAudioSource } from "../audioSource";
import {
    clipReferenceAuthoring,
    clipReferenceTags,
} from "../clipReferenceAuthoring";
import {
    clipRelationship,
    SOURCE_AUDIO_RELATIONSHIP,
    SOURCE_VIDEO_RELATIONSHIP,
} from "../clipRelationships";
import { executableBoundaries } from "../clipSemantics";
import { buildBaseAudioFields } from "../detailStrip/audioPanel";
import { buildBoundaryFields } from "../detailStrip/boundaryPanel";
import { applyPersistedCapabilityRepair } from "../detailStrip/capabilityUi";
import { buildClipColumn } from "../detailStrip/clipBasics";
import { buildClipLoraControls } from "../detailStrip/clipLorasPanel";
import {
    buildClipReferenceFields,
    buildIncomingReferenceFields,
} from "../detailStrip/clipReferencePanel";
import type { DetailStripContext } from "../detailStrip/context";
import { buildInitVideoFields } from "../detailStrip/initVideoPanel";
import {
    buildMajorPromptFields,
    buildRelayPromptFields,
} from "../detailStrip/promptPanels";
import { buildKeyframeFields } from "../detailStrip/refPanel";
import { buildRetakeFields } from "../detailStrip/retakePanel";
import { buildSettingsFields } from "../detailStrip/settingsPanel";
import { buildStageParamsColumn } from "../detailStrip/stagePanel";
import {
    buildResetVideoStagesButton,
    buildVideoStagesSettingsFields,
} from "../detailStrip/videoStagesSettings";
import { buildField } from "../detailWidgets";
import { H3_TEXT_ENCODER_FEATURE } from "../generatedMiniMaxTextEncoder";
import { H3_ATTENTION_WINDOW_FEATURE } from "../h3AttentionWindow";
import { getVideoStagesHostBridge } from "../host";
import {
    appendLoraToClip,
    defaultLoraWeight,
    removeLoraAt,
} from "../loraAuthoring";
import { TIMELINE_AUTHORING_SETTINGS_CHANGED } from "../timelineAuthoringSettings";
import { incomingReferenceContinueForClip } from "../timelineTiming";
import type { AuthoringDocument, Clip, TimelineSelection } from "../types";
import {
    type EditorAction,
    type EditorItem,
    type EditorSection,
    element,
    scopeFields,
} from "./components";
import { bindReferenceDrop } from "./referenceDrops";

export function clipSections(
    state: AuthoringDocument,
    clipIdx: number,
    context: DetailStripContext,
): EditorSection[] {
    const clip = state.clips[clipIdx];
    const snapshot = context.authoring();
    const capabilities = snapshot.capabilities.forClip(clip);
    const singleton = (
        key: string,
        label: string,
        build: (ctx: DetailStripContext) => HTMLElement | HTMLElement[],
        selection?: TimelineSelection,
    ): EditorSection => ({
        key,
        label,
        items: [
            {
                key: `${clip.id}/${key}`,
                label,
                selection,
                fields: () => build(scopeFields(context, `${clip.id}/${key}`)),
            },
        ],
    });
    const remove = (run: () => void): EditorAction => ({
        key: "remove-item",
        label: "Delete",
        kind: "delete",
        run,
    });
    const repeated = <T extends { id?: string }>(
        key: string,
        label: string,
        entries: readonly T[],
        build: (
            ctx: DetailStripContext,
            entry: T,
            index: number,
        ) => HTMLElement,
        selection: (index: number) => TimelineSelection,
        add: () => void,
        removeAt: (index: number) => void,
        visible = true,
        layout: EditorSection["layout"] = "cards",
    ): EditorSection[] =>
        visible
            ? [
                  {
                      key,
                      label,
                      layout,
                      actions: [
                          {
                              key: `add-${key}`,
                              label: `+ Add ${label === "Stages" ? "stage" : label === "Keyframes" ? "keyframe" : label === "References" ? "reference" : label === "Relay prompts" ? "relay prompt" : label === "LoRAs" ? "LoRA" : "IC-LoRA"}`,
                              kind: "add",
                              run: add,
                          },
                      ],
                      items: entries.map((entry, index): EditorItem => {
                          const scope = `${clip.id}/${key}/${entry.id || index}`;
                          const contextLabel =
                              key === "stages" ? `S${index}` : String(index);
                          return {
                              key: scope,
                              contextLabel,
                              label:
                                  key === "stages"
                                      ? `Stage ${contextLabel}`
                                      : `${label} ${contextLabel}`,
                              selection: selection(index),
                              fields: () =>
                                  build(
                                      scopeFields(context, scope),
                                      entry,
                                      index,
                                  ),
                              actions:
                                  key === "stages" && index === 0
                                      ? []
                                      : [remove(() => removeAt(index))],
                          };
                      }),
                  },
              ]
            : [];
    const structural = (edit: (target: Clip) => void): void =>
        context.structuralCommit((clips) => {
            const target = clips[clipIdx];
            if (!target) return null;
            edit(target);
            return "render";
        });
    const prompt = singleton(
        "prompt",
        "Prompt & clip",
        (ctx) => [
            buildMajorPromptFields(ctx, clip, clipIdx, { rows: 3 }),
            buildClipColumn(
                ctx,
                clip,
                clipIdx,
                capabilities.authoringState(
                    "referenceFraming",
                    clip.refFraming !== "crop",
                ),
                capabilities.architectureId === "minimax" &&
                    getVideoStagesHostBridge().hasBackendFeature(
                        H3_ATTENTION_WINDOW_FEATURE,
                    ),
                capabilities.architectureId !== "minimax"
                    ? "hidden"
                    : getVideoStagesHostBridge().hasBackendFeature(
                            H3_TEXT_ENCODER_FEATURE,
                        )
                      ? "ready"
                      : "install",
                capabilities.audioSourceKinds.includes("Native"),
            ),
        ],
        { kind: "prompt-major", clipIdx },
    );
    if (clipIdx > 0)
        prompt.actions = [
            {
                key: "toggle-clip",
                label: clip.skipped ? "Include clip" : "Skip clip",
                run: () => context.toggleClipSkip(clipIdx),
            },
        ];
    const stages = repeated(
        "stages",
        "Stages",
        clip.stages,
        (ctx, entry, index) =>
            buildStageParamsColumn(
                ctx,
                clip,
                clipIdx,
                index,
                entry,
                snapshot.defaults,
            ),
        (index) => ({ kind: "clip", clipIdx, stageIdx: index }),
        () => context.addStage(clipIdx),
        (index) => context.deleteStage(clipIdx, index),
    );
    for (const [index, item] of stages[0].items.entries()) {
        if (index > 0)
            item.toggle = {
                key: "toggle-stage",
                label: `Enable stage S${index}`,
                checked: !clip.stages[index].skipped,
                run: () => context.toggleStageSkip(clipIdx, index),
            };
    }
    const sections = [
        prompt,
        ...stages,
        ...repeated(
            "loras",
            "LoRAs",
            clip.loras.map((entry, index) => ({
                ...entry,
                id: `lora-${index}`,
            })),
            (ctx, _entry, index) => {
                const controls = buildClipLoraControls(
                    ctx,
                    clip,
                    clipIdx,
                    index,
                    snapshot.defaults,
                );
                const fields = element("div", "vst-detail-fields");
                fields.append(
                    buildField("Model", controls.model),
                    buildField("Weight", controls.weight),
                );
                return fields;
            },
            () => ({ kind: "clip", clipIdx, stageIdx: 0 }),
            () =>
                structural((target) => {
                    const name = snapshot.defaults.loraValues.find(
                        (value) =>
                            !target.loras.some((lora) => lora.name === value),
                    );
                    if (name)
                        appendLoraToClip(
                            target,
                            name,
                            defaultLoraWeight(snapshot.defaults, name),
                        );
                }),
            (index) =>
                structural((target) => {
                    removeLoraAt(target, index);
                }),
            true,
            "rows",
        ),
        ...repeated(
            "keyframes",
            "Keyframes",
            clip.frameRefs,
            (ctx, entry, index) =>
                buildKeyframeFields(
                    ctx,
                    clip,
                    clipIdx,
                    entry,
                    index,
                    state.fps,
                ),
            (index) => ({ kind: "ref", clipIdx, refIdx: index }),
            () => context.addRefEntry(clipIdx),
            (index) => context.deleteRefEntry(clipIdx, index),
            capabilities.decision("frameReferences").supported ||
                clip.frameRefs.length > 0,
            "rows",
        ),
        ...repeated(
            "references",
            "References",
            clip.references,
            (ctx, _entry, index) =>
                buildClipReferenceFields(
                    ctx,
                    clipIdx,
                    index,
                    state.clips,
                    state.fps,
                ),
            (index) => ({ kind: "clip-ref", clipIdx, referenceIdx: index }),
            () => context.addClipReference(clipIdx),
            (index) => context.deleteClipReference(clipIdx, index),
            clipReferenceAuthoring(capabilities).decision.supported ||
                clip.references.length > 0,
            "rows",
        ),
        ...repeated(
            "ic-loras",
            "IC-LoRAs",
            clip.icLoras,
            (ctx, _entry, index) =>
                buildArchitectureIcLoraFields(
                    ctx,
                    clip,
                    clipIdx,
                    snapshot.defaults,
                    index,
                ),
            (index) => ({ kind: "ic-lora", clipIdx, entryIdx: index }),
            () =>
                structural((target) => {
                    appendDefaultIcLora(target, snapshot.defaults);
                }),
            (index) =>
                structural((target) => {
                    removeIcLoraEntryAt(target, index);
                }),
            capabilities.decision("icLora").supported ||
                clip.icLoras.length > 0,
            "rows",
        ),
        ...repeated(
            "relay-prompts",
            "Relay prompts",
            clip.promptWindows,
            (ctx, _entry, index) =>
                buildRelayPromptFields(ctx, clip, clipIdx, index),
            (index) => ({ kind: "prompt-minor", clipIdx, windowIdx: index }),
            () => context.addPromptWindow(clipIdx),
            (index) => context.deleteWindowEntry(clipIdx, index),
        ),
        singleton(
            "source-video",
            "Source video",
            (ctx) =>
                buildInitVideoFields(
                    ctx,
                    clip,
                    clipIdx,
                    (clipIdx === 0 && clip.audioOnly === true) ||
                        (clipIdx === 1 && state.clips[0]?.audioOnly === true),
                ),
            { kind: "clip", clipIdx, stageIdx: 0 },
        ),
        ...(capabilities.clipAudio.supported ||
        clip.uploadedAudio ||
        clip.audioSource !==
            defaultAuthoringAudioSource(capabilities.audioSourceKinds) ||
        clip.reuseAudio ||
        clip.clipLengthFromAudio ||
        clip.clipLengthFromControlNet ||
        clip.saveAudioTrack
            ? [
                  singleton(
                      "audio",
                      "Audio",
                      (ctx) => buildBaseAudioFields(ctx, clipIdx, state),
                      { kind: "audio", clipIdx },
                  ),
              ]
            : []),
        ...(capabilities.decision("retake").supported || clip.retake
            ? [
                  singleton(
                      "retake",
                      "Retake",
                      (ctx) => buildRetakeFields(ctx, clip, clipIdx),
                      { kind: "retake", clipIdx },
                  ),
              ]
            : []),
    ];
    if (clipIdx > 0) {
        const preceding = clipIdx - 1;
        const relationship = clipRelationship(state.clips, preceding);
        const leftClipIdx =
            relationship === SOURCE_AUDIO_RELATIONSHIP ||
            relationship === SOURCE_VIDEO_RELATIONSHIP
                ? preceding
                : (executableBoundaries(state.clips).find(
                      (boundary) => boundary.rightIdx === clipIdx,
                  )?.leftIdx ?? preceding);
        sections.push(
            singleton(
                "join",
                "Join from previous clip",
                (ctx) =>
                    buildBoundaryFields(
                        ctx,
                        { kind: "boundary", leftClipIdx },
                        state,
                    ),
                { kind: "boundary", leftClipIdx },
            ),
        );
    }
    sections.push(
        singleton("output", "Output settings", (ctx) =>
            buildSettingsFields(ctx, state),
        ),
    );
    sections.push(
        singleton("settings", "VideoStages settings", (ctx) => {
            const fields = buildVideoStagesSettingsFields(() => {
                window.dispatchEvent(
                    new Event(TIMELINE_AUTHORING_SETTINGS_CHANGED),
                );
                ctx.render();
            });
            fields.append(buildResetVideoStagesButton(ctx.render));
            return fields;
        }),
    );
    const incoming =
        clipReferenceAuthoring(capabilities).mode === "all"
            ? incomingReferenceContinueForClip(
                  state.clips,
                  state.fps,
                  snapshot.capabilities,
                  clipIdx,
              )
            : null;
    const references = sections.find((entry) => entry.key === "references");
    if (references) {
        const preceding = incoming
            ? [
                  {
                      kind: "video" as const,
                      includeSoundtrack:
                          state.clips[incoming.leftIdx]
                              .boundaryOutReferenceIncludeSoundtrack,
                  },
              ]
            : [];
        const tags = clipReferenceTags(clip.references, preceding);
        for (const [index, item] of references.items.entries()) {
            item.label = tags[index];
            item.contextLabel = tags[index];
        }
        if (incoming)
            references.items.unshift({
                key: `${clip.id}/incoming-reference`,
                label: `<Video 1> · From Clip ${incoming.leftIdx}`,
                contextLabel: `<Video 1> · From Clip ${incoming.leftIdx}`,
                selection: {
                    kind: "boundary-ref",
                    leftClipIdx: incoming.leftIdx,
                },
                fields: () =>
                    buildIncomingReferenceFields(
                        scopeFields(context, `${clip.id}/incoming-reference`),
                        clipIdx,
                        state.clips,
                        state.fps,
                    ),
            });
    }
    const retake = sections.find((entry) => entry.key === "retake");
    if (retake && clip.retake)
        retake.actions = [remove(() => context.removeRetake(clipIdx))];
    const decisions: Record<string, CapabilityDecision> = {
        keyframes: capabilities.decision("frameReferences"),
        references: clipReferenceAuthoring(capabilities).decision,
        "ic-loras": capabilities.decision("icLora"),
        retake: capabilities.decision("retake"),
        "relay-prompts": capabilities.decision("promptRelay"),
    };
    for (const section of sections) {
        const decision = decisions[section.key];
        if (decision && !decision.supported) {
            for (const action of section.actions ?? []) {
                if (action.kind === "add") {
                    action.disabled = true;
                    action.title = decision.reason;
                }
            }
            for (const item of section.items) {
                const build = item.fields;
                item.fields = () => {
                    const fields = build();
                    for (const field of Array.isArray(fields)
                        ? fields
                        : [fields])
                        applyPersistedCapabilityRepair(field, decision);
                    return fields;
                };
            }
        }
    }
    const relay = sections.findIndex((entry) => entry.key === "relay-prompts");
    if (
        !decisions["relay-prompts"].supported &&
        !clip.promptWindows.length &&
        relay >= 0
    )
        sections.splice(relay, 1);
    const disableAdd = (key: string, reason: string): void => {
        const add = sections
            .find((entry) => entry.key === key)
            ?.actions?.find((action) => action.kind === "add");
        if (add) {
            add.disabled = true;
            add.title = reason;
        }
    };
    if (clipIdx === 0 && clip.audioOnly) {
        disableAdd(
            "stages",
            "Audio-only source clips do not use video stages.",
        );
        disableAdd(
            "keyframes",
            "Audio-only source clips do not use keyframes.",
        );
    }
    if (
        !referenceEndpointPolicy(clip, snapshot.defaults.modelCatalog).available
    )
        disableAdd(
            "keyframes",
            "The selected models do not publish a supported keyframe endpoint.",
        );
    if (
        !snapshot.defaults.loraValues.some(
            (name) => !clip.loras.some((lora) => lora.name === name),
        )
    )
        disableAdd("loras", "All available LoRAs are already on this clip.");
    const clipId = clip.id;
    if (context.addRefEntries && context.addClipReferences && clipId) {
        const operations = {
            addRefEntries: context.addRefEntries,
            addClipReferences: context.addClipReferences,
        };
        for (const [key, kind] of [
            ["keyframes", "keyframe"],
            ["references", "clip-reference"],
        ] as const) {
            const add = sections
                .find((entry) => entry.key === key)
                ?.actions?.find((action) => action.kind === "add");
            if (add)
                add.configure = (button) =>
                    bindReferenceDrop(button, clipId, kind, operations);
        }
    }
    return sections;
}
