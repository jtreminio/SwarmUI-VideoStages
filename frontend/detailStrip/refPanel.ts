import {
    boundedReferencePositionHelp,
    boundedReferenceToggleHelp,
    referenceEndpointPolicy,
} from "../architectures/referenceEndpoints";
import { clamp, mediaPreviewSrc, REF_FRAME_MIN } from "../constants";
import {
    buildCheckbox,
    buildField,
    buildMediaPickRow,
    buildNumber,
    buildOptionSelect,
    buildRepeatingEditor,
} from "../detailWidgets";
import {
    buildImageSourceOptions,
    resolveImageSourceValue,
} from "../imageSource";
import { getReferenceFrameMax } from "../normalizationStage";
import { setSelection } from "../selection";
import {
    type Clip,
    REF_SOURCE_UPLOAD,
    type TimelineSelection,
    type VideoStagesConfig,
} from "../types";
import type { DetailStripContext } from "./context";

/**
 * Stage-style frame-reference child section for the owning clip panel. The
 * rail lists every reference while only the selected reference editor renders.
 */
export const buildRefSection = (
    ctx: DetailStripContext,
    clipIdx: number,
    selectedRefIdx: number | null,
    clips: Clip[],
    fps: number,
    open = selectedRefIdx !== null,
): HTMLElement => {
    const clip = clips[clipIdx];
    const { capabilities, defaults } = ctx.authoring();
    const decision = capabilities.forClip(clip).decision("frameReferences");
    const endpointPolicy = referenceEndpointPolicy(clip, defaults.modelCatalog);
    const hasSupportedEndpoint = endpointPolicy.available;
    const activeRefIdx =
        clip.frameRefs.length === 0
            ? null
            : clamp(selectedRefIdx ?? 0, 0, clip.frameRefs.length - 1);
    const buildSection = (
        editorForItem?: (index: number) => HTMLElement | undefined,
    ): HTMLElement =>
        buildRepeatingEditor({
            key: "references",
            label: "Frame References",
            sectionClass: "vst-detail-ref-section",
            open,
            items: clip.frameRefs.map((_, refIdx) => ({
                label: `Ref${refIdx}`,
                focusKey: `reference-tab-${refIdx}`,
                title: `Edit frame reference ${refIdx}`,
                active: refIdx === activeRefIdx,
                className: "vst-ref-tab",
                onSelect: () => setSelection({ kind: "ref", clipIdx, refIdx }),
                onDelete: () => ctx.deleteRefEntry(clipIdx, refIdx),
            })),
            add: {
                title: !decision.supported
                    ? decision.reason
                    : hasSupportedEndpoint
                      ? "Add a frame reference"
                      : "The selected models do not publish a supported frame-reference endpoint.",
                label: "+ Add Frame Reference",
                className: "vst-detail-add-ref",
                disabled: !decision.supported || !hasSupportedEndpoint,
                onClick: () => ctx.addRefEntry(clipIdx),
            },
            remove: {
                title:
                    activeRefIdx === null
                        ? "No frame reference to delete"
                        : `Delete frame reference ${activeRefIdx}`,
                className: "vst-detail-delete-ref",
            },
            editorForItem,
        }).section;

    if (activeRefIdx === null) {
        return buildSection();
    }
    const buildEditor = (editorRefIdx: number): HTMLElement | undefined => {
        const ref = clip.frameRefs[editorRefIdx];
        if (!ref) {
            return undefined;
        }
        const options = buildImageSourceOptions(ref.source ?? "");
        const source = resolveImageSourceValue(ref.source ?? "", options);
        const isUpload = source === REF_SOURCE_UPLOAD;
        const fields = document.createElement("div");
        fields.className =
            "vst-detail-col vst-detail-instance-fields vst-detail-ref-row vst-detail-ref-editor";
        fields.setAttribute("data-vst-ref-index", `${editorRefIdx}`);

        const select = buildOptionSelect(options, source, (value) => {
            ctx.commit((cs) => {
                const target = cs[clipIdx]?.frameRefs[editorRefIdx];
                if (!target) {
                    return;
                }
                const resolved = resolveImageSourceValue(
                    value,
                    buildImageSourceOptions(value),
                );
                target.source = resolved;
                if (resolved !== REF_SOURCE_UPLOAD) {
                    target.uploadedImage = null;
                    target.uploadFileName = null;
                }
            });
            ctx.render();
        });
        fields.appendChild(
            buildField(
                "Image Source",
                select,
                undefined,
                "Where this reference image comes from — an upload, or another " +
                    "clip's rendered frame. The image guides how the clip looks " +
                    "at its attach frame.",
            ),
        );

        if (isUpload) {
            const preview = document.createElement("div");
            preview.className = "vst-refs-thumb-preview";
            const data = ref.uploadedImage?.data;
            if (data) {
                preview.style.backgroundImage = `url('${mediaPreviewSrc(data)}')`;
                preview.classList.add("vst-refs-thumb-preview-set");
            }
            fields.appendChild(preview);
        }

        const frameMax = getReferenceFrameMax(() => defaults, clip, fps);
        const boundedPositions = endpointPolicy.bounded;
        const supportsFirst = endpointPolicy.supportsFirst;
        const supportsLast = endpointPolicy.supportsLast;
        const currentPositionSupported = ref.fromEnd
            ? supportsLast
            : supportsFirst;
        const editableFrameMax = boundedPositions ? REF_FRAME_MIN : frameMax;
        const frameInput = buildNumber(
            ref.frame,
            REF_FRAME_MIN,
            editableFrameMax,
            1,
            (value) => {
                ctx.debouncedCommit(`ref-${editorRefIdx}-frame`, (cs) => {
                    const target = cs[clipIdx]?.frameRefs[editorRefIdx];
                    if (target) {
                        target.frame = clamp(
                            Math.round(value),
                            REF_FRAME_MIN,
                            editableFrameMax,
                        );
                    }
                });
            },
        );
        frameInput.setAttribute(
            "data-vst-focus-key",
            `ref-${editorRefIdx}-frame`,
        );
        frameInput.disabled = !hasSupportedEndpoint;
        fields.appendChild(
            buildField(
                "Attach at Frame",
                frameInput,
                undefined,
                boundedReferencePositionHelp(endpointPolicy) ??
                    (boundedPositions
                        ? "This clip accepts an image only at a bounded endpoint."
                        : "The frame within the clip where this reference is anchored. " +
                          "Frame 1 is the first frame; the image influences the clip " +
                          "most strongly around here."),
            ),
        );
        fields.appendChild(
            buildCheckbox(
                "Count from clip end",
                ref.fromEnd === true,
                (value) => {
                    ctx.commit((cs) => {
                        const target = cs[clipIdx]?.frameRefs[editorRefIdx];
                        if (target) {
                            target.fromEnd = value;
                        }
                    });
                },
                {
                    disabled:
                        !hasSupportedEndpoint ||
                        (boundedPositions &&
                            currentPositionSupported &&
                            !(supportsFirst && supportsLast)),
                    help:
                        boundedReferenceToggleHelp(endpointPolicy) ??
                        (boundedPositions
                            ? "Choose the supported clip endpoint."
                            : "Count the attach frame backwards from the last frame " +
                              "instead of forward from the first — so it stays " +
                              "anchored to the end even if the clip length changes."),
                },
            ),
        );

        if (isUpload) {
            fields.appendChild(
                buildMediaPickRow(
                    "Image Upload",
                    "image/*",
                    ["image"],
                    ref.uploadedImage?.fileName,
                    (data, fileName) => {
                        ctx.commit((cs) => {
                            const target = cs[clipIdx]?.frameRefs[editorRefIdx];
                            if (target) {
                                target.uploadedImage = { data, fileName };
                                target.uploadFileName = fileName;
                            }
                        });
                        ctx.render();
                    },
                    () => {
                        ctx.commit((cs) => {
                            const target = cs[clipIdx]?.frameRefs[editorRefIdx];
                            if (target) {
                                target.uploadedImage = null;
                                target.uploadFileName = null;
                            }
                        });
                        ctx.render();
                    },
                ),
            );
        }
        return fields;
    };
    return buildSection(buildEditor);
};

/** Standalone wrapper retained for focused panel tests and integrations. */
export const buildRefBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "ref" }>,
    state: VideoStagesConfig,
): HTMLElement => {
    const body = document.createElement("div");
    body.className = "vst-detail-body";
    body.appendChild(
        buildRefSection(ctx, sel.clipIdx, sel.refIdx, state.clips, state.fps),
    );
    return body;
};
