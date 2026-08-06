import { runClipMediaProbe } from "../clipMediaProbeGuard";
import {
    CLIP_REFERENCE_KIND_INFO,
    CLIP_REFERENCE_KINDS,
    CLIP_REFERENCE_SCALES,
    clipReferenceCanDriveLength,
    clipReferenceTags,
} from "../clipReferenceAuthoring";
import { CLIP_DURATION_MIN, clamp, mediaPreviewSrc } from "../constants";
import {
    buildCheckbox,
    buildField,
    buildMediaPickRow,
    buildOptionSelect,
    buildRepeatingEditor,
} from "../detailWidgets";
import {
    buildImageSourceOptions,
    resolveImageSourceValue,
} from "../imageSource";
import { probeMediaDurationSeconds } from "../initVideoProbe";
import { setSelection } from "../selection";
import { applyClipDurationResize } from "../timelineEdit";
import { incomingReferenceContinueForClip } from "../timelineTiming";
import {
    type Clip,
    type ClipReference,
    type ClipReferenceKind,
    REF_SOURCE_UPLOAD,
    type RootDefaults,
} from "../types";
import { roundToTenth } from "../utils";
import type { DetailStripContext } from "./context";

/** Takes clip-length ownership away from every other claimant and applies it. */
const claimClipLength = (
    clip: Clip,
    referenceIdx: number,
    getDefaults: () => RootDefaults,
    fps: number,
): void => {
    clip.references.forEach((reference, index) => {
        reference.drivesClipLength = index === referenceIdx;
    });
    if (referenceIdx < 0) {
        return;
    }
    // The two clip-level flags derive the length at generation time, which
    // would silently move a clip whose authored length now says otherwise.
    clip.clipLengthFromAudio = false;
    clip.clipLengthFromControlNet = false;
    const seconds = clip.references[referenceIdx]?.mediaDurationSeconds ?? 0;
    if (seconds > 0) {
        applyClipDurationResize(
            clip,
            Math.max(CLIP_DURATION_MIN, seconds),
            getDefaults,
            fps,
        );
    }
};

/** Timed media is probed for the length it can lend, which lands with it in one save. */
const applyPickedReferenceMedia = (
    ctx: DetailStripContext,
    clipId: string,
    referenceId: string,
    data: string,
    fileName: string,
): void =>
    runClipMediaProbe({
        clipId,
        slot: referenceId,
        probe: () => probeMediaDurationSeconds(data),
        apply: (clip, seconds, state) => {
            const index = clip.references.findIndex(
                (reference) => reference.id === referenceId,
            );
            const reference = clip.references[index];
            if (!reference) {
                return;
            }
            reference.uploadedMedia = { data, fileName };
            reference.mediaDurationSeconds = roundToTenth(seconds);
            if (reference.drivesClipLength) {
                claimClipLength(
                    clip,
                    index,
                    () => ctx.authoring().defaults,
                    state.fps,
                );
            }
        },
        onApplied: () => ctx.render(),
    });

/**
 * Whole-clip references: media the model conditions on with no frame position.
 * Each row shows the prompt tag the reference is presented under, because the
 * prompt has to name it for the reference to do anything.
 */
export const buildClipReferenceSection = (
    ctx: DetailStripContext,
    clipIdx: number,
    selectedIdx: number | null,
    clips: Clip[],
    fps: number,
    open = selectedIdx !== null,
    incomingSelected = false,
): HTMLElement => {
    const clip = clips[clipIdx];
    const references = clip.references;
    const { capabilities, defaults } = ctx.authoring();
    const decision = capabilities.forClip(clip).decision("clipReferences");
    const incomingBoundary = incomingReferenceContinueForClip(
        clips,
        fps,
        capabilities,
        clipIdx,
    );
    const incomingReference = {
        kind: "video" as const,
        includeSoundtrack: incomingBoundary
            ? clips[incomingBoundary.leftIdx]
                  .boundaryOutReferenceIncludeSoundtrack
            : false,
    };
    const tags = clipReferenceTags(
        references,
        incomingBoundary ? [incomingReference] : [],
    );
    const itemOffset = incomingBoundary ? 1 : 0;
    const activeIdx =
        references.length === 0 || selectedIdx === null
            ? null
            : clamp(selectedIdx, 0, references.length - 1);
    const buildIncomingEditor = (): HTMLElement | undefined => {
        if (!incomingBoundary) {
            return undefined;
        }
        const sourceClip = clips[incomingBoundary.leftIdx];
        const patchIncoming = (mutate: (source: Clip) => void): void => {
            ctx.commit((cs) => {
                const source = cs[incomingBoundary.leftIdx];
                if (source) {
                    mutate(source);
                }
            });
        };
        const fields = document.createElement("div");
        fields.className =
            "vst-detail-col vst-detail-instance-fields vst-detail-clip-ref-editor vst-detail-join-ref-editor";
        fields.appendChild(
            buildField(
                "Reference scale",
                buildOptionSelect(
                    CLIP_REFERENCE_SCALES.map((scale) => ({
                        value: `${scale.value}`,
                        label: scale.label,
                    })),
                    `${sourceClip.boundaryOutReferenceScale}`,
                    (value) => {
                        patchIncoming((source) => {
                            source.boundaryOutReferenceScale = Number(value);
                        });
                    },
                ),
            ),
        );
        fields.appendChild(
            buildCheckbox(
                "Include soundtrack",
                sourceClip.boundaryOutReferenceIncludeSoundtrack,
                (value) => {
                    patchIncoming((source) => {
                        source.boundaryOutReferenceIncludeSoundtrack = value;
                    });
                    ctx.render();
                },
            ),
        );
        return fields;
    };
    const buildSection = (
        editorForItem?: (index: number) => HTMLElement | undefined,
    ): HTMLElement =>
        buildRepeatingEditor({
            key: "clip-references",
            label: "References",
            sectionClass: "vst-detail-clip-ref-section",
            open,
            items: [
                ...(incomingBoundary
                    ? [
                          {
                              label: `<Video 1> (from Join with Clip ${incomingBoundary.leftIdx})`,
                              title: `Edit the reference supplied by the Continue join from Clip ${incomingBoundary.leftIdx}`,
                              focusKey: `clip-reference-join-${incomingBoundary.leftIdx}`,
                              active: incomingSelected,
                              className:
                                  "vst-clip-ref-tab vst-clip-ref-join-tab",
                              onSelect: () =>
                                  setSelection({
                                      kind: "boundary-ref",
                                      leftClipIdx: incomingBoundary.leftIdx,
                                  }),
                          },
                      ]
                    : []),
                ...references.map((reference, index) => ({
                    label: tags[index],
                    focusKey: `clip-reference-tab-${index}`,
                    title: `Edit ${CLIP_REFERENCE_KIND_INFO[reference.kind].label} reference ${tags[index]}`,
                    active: index === activeIdx,
                    className: "vst-clip-ref-tab",
                    onSelect: () =>
                        setSelection({
                            kind: "clip-ref",
                            clipIdx,
                            referenceIdx: index,
                        }),
                    onShiftDelete: () =>
                        ctx.deleteClipReference(clipIdx, index),
                })),
            ],
            add: {
                title: decision.supported
                    ? "Add a reference the prompt can name by tag"
                    : decision.reason,
                label: "+ Add Reference",
                className: "vst-detail-add-clip-ref",
                disabled: !decision.supported,
                onClick: () => ctx.addClipReference(clipIdx),
            },
            remove: {
                title:
                    activeIdx === null
                        ? "No reference to delete"
                        : `Delete reference ${tags[activeIdx]}`,
                className: "vst-detail-delete-clip-ref",
            },
            editorForItem:
                incomingBoundary === null && editorForItem === undefined
                    ? undefined
                    : (itemIndex) => {
                          if (itemIndex < itemOffset) {
                              return buildIncomingEditor();
                          }
                          return editorForItem?.(itemIndex - itemOffset);
                      },
        }).section;

    if (activeIdx === null) {
        return buildSection();
    }
    const buildEditor = (editorIdx: number): HTMLElement | undefined => {
        const reference = references[editorIdx];
        if (!reference) {
            return undefined;
        }
        const patch = (mutate: (target: ClipReference) => void): void => {
            ctx.commit((cs) => {
                const target = cs[clipIdx]?.references[editorIdx];
                if (target) {
                    mutate(target);
                }
            });
        };
        const fields = document.createElement("div");
        fields.className =
            "vst-detail-col vst-detail-instance-fields vst-detail-clip-ref-editor";
        fields.setAttribute("data-vst-clip-ref-index", `${editorIdx}`);

        fields.appendChild(
            buildField(
                "Kind",
                buildOptionSelect(
                    CLIP_REFERENCE_KINDS.map((kind) => ({
                        value: kind,
                        label: CLIP_REFERENCE_KIND_INFO[kind].label,
                    })),
                    reference.kind,
                    (value) => {
                        patch((target) => {
                            target.kind = value as ClipReferenceKind;
                            target.uploadedMedia = null;
                            target.includeSoundtrack = false;
                            target.mediaDurationSeconds = 0;
                            target.drivesClipLength = false;
                            if (target.kind !== "image") {
                                target.source = REF_SOURCE_UPLOAD;
                            }
                        });
                        ctx.render();
                    },
                ),
                undefined,
                `What this reference is. The prompt names it as ${tags[editorIdx]} — ` +
                    "a reference the prompt never mentions still costs sampling time.",
            ),
        );

        const options = buildImageSourceOptions(reference.source ?? "");
        const source = resolveImageSourceValue(reference.source ?? "", options);
        if (reference.kind === "image") {
            fields.appendChild(
                buildField(
                    "Image Source",
                    buildOptionSelect(options, source, (value) => {
                        patch((target) => {
                            const resolved = resolveImageSourceValue(
                                value,
                                buildImageSourceOptions(value),
                            );
                            target.source = resolved;
                            if (resolved !== REF_SOURCE_UPLOAD) {
                                target.uploadedMedia = null;
                            }
                        });
                        ctx.render();
                    }),
                    undefined,
                    "Where this reference image comes from — an upload, or " +
                        "another clip's rendered frame.",
                ),
            );
        }

        if (reference.kind !== "image" || source === REF_SOURCE_UPLOAD) {
            const data = reference.uploadedMedia?.data;
            if (reference.kind === "image" && data) {
                const preview = document.createElement("div");
                preview.className =
                    "vst-refs-thumb-preview vst-refs-thumb-preview-set";
                preview.style.backgroundImage = `url('${mediaPreviewSrc(data)}')`;
                fields.appendChild(preview);
            }
            const media = CLIP_REFERENCE_KIND_INFO[reference.kind];
            fields.appendChild(
                buildMediaPickRow(
                    `${media.label} File`,
                    media.accept,
                    [...media.browserTypes],
                    reference.uploadedMedia?.fileName,
                    (pickedData, fileName) => {
                        if (
                            clipReferenceCanDriveLength(reference) &&
                            clip.id &&
                            reference.id
                        ) {
                            applyPickedReferenceMedia(
                                ctx,
                                clip.id,
                                reference.id,
                                pickedData,
                                fileName,
                            );
                            return;
                        }
                        patch((target) => {
                            target.uploadedMedia = {
                                data: pickedData,
                                fileName,
                            };
                        });
                        ctx.render();
                    },
                    () => {
                        patch((target) => {
                            target.uploadedMedia = null;
                            target.mediaDurationSeconds = 0;
                            target.drivesClipLength = false;
                        });
                        ctx.render();
                    },
                ),
            );
        }

        if (clipReferenceCanDriveLength(reference)) {
            const seconds = reference.mediaDurationSeconds;
            const hint = document.createElement("small");
            hint.className = "vst-detail-field-hint";
            hint.textContent = `Detected: ${
                seconds > 0 ? `${seconds.toFixed(1)} s` : "unknown length"
            }`;
            fields.appendChild(hint);
            fields.appendChild(
                buildCheckbox(
                    `Clip Length from ${CLIP_REFERENCE_KIND_INFO[reference.kind].label}`,
                    reference.drivesClipLength === true,
                    (value) => {
                        ctx.commit((cs) => {
                            const target = cs[clipIdx];
                            if (target) {
                                claimClipLength(
                                    target,
                                    value ? editorIdx : -1,
                                    () => defaults,
                                    fps,
                                );
                            }
                        });
                        ctx.render();
                    },
                    {
                        // Never disable a ticked box: a re-pick that probes to
                        // nothing would otherwise trap an unhonourable claim.
                        disabled:
                            !(seconds > 0) &&
                            reference.drivesClipLength !== true,
                        help:
                            seconds > 0
                                ? "Set this clip's length to the reference's own " +
                                  "length. Only one source can own the clip length, " +
                                  "so this clears any other reference and the " +
                                  "clip-level length options."
                                : "This reference has no detected length to lend " +
                                  "the clip. Pick a file the browser can read.",
                    },
                ),
            );
        }

        if (reference.kind === "video") {
            fields.appendChild(
                buildField(
                    "Reference scale",
                    buildOptionSelect(
                        CLIP_REFERENCE_SCALES.map((scale) => ({
                            value: `${scale.value}`,
                            label: scale.label,
                        })),
                        `${reference.mediaScale}`,
                        (value) => {
                            patch((target) => {
                                target.mediaScale = Number(value);
                            });
                        },
                    ),
                    undefined,
                    "Downsample this video before it is referenced. Its tokens " +
                        "are re-encoded on every sampling step, so half or " +
                        "quarter resolution is markedly faster and costs detail " +
                        "the model may not need from a motion or style reference.",
                ),
            );
            fields.appendChild(
                buildCheckbox(
                    "Include soundtrack",
                    reference.includeSoundtrack === true,
                    (value) => {
                        patch((target) => {
                            target.includeSoundtrack = value;
                        });
                        ctx.render();
                    },
                    {
                        help:
                            "Also reference this video's own audio. It is " +
                            "presented as its own Audio reference just before " +
                            `${tags[editorIdx]}, ahead of every standalone audio ` +
                            "reference — the tags shown here already account for it.",
                    },
                ),
            );
        }
        return fields;
    };
    return buildSection(buildEditor);
};
