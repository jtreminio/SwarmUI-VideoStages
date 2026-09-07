import {
    boundedReferencePositionHelp,
    boundedReferenceToggleHelp,
    referenceEndpointPolicy,
} from "../architectures/referenceEndpoints";
import { clamp, REF_FRAME_MIN } from "../constants";
import {
    buildCheckbox,
    buildField,
    buildMediaPickRow,
    buildNumber,
    buildOptionSelect,
} from "../detailWidgets";
import { MEDIA_SOURCE_UPLOAD } from "../generatedMediaSource";
import {
    buildImageSourceOptions,
    resolveImageSourceValue,
} from "../imageSource";
import { getReferenceFrameMax } from "../normalizationStage";
import type { Clip, FrameRefImage } from "../types";
import type { DetailStripContext } from "./context";

export const buildKeyframeFields = (
    ctx: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    ref: FrameRefImage,
    refIdx: number,
    fps: number,
): HTMLElement => {
    const { defaults } = ctx.authoring();
    const endpointPolicy = referenceEndpointPolicy(clip, defaults.modelCatalog);
    const hasSupportedEndpoint = endpointPolicy.available;
    const options = buildImageSourceOptions(ref.source ?? "");
    const source = resolveImageSourceValue(ref.source ?? "", options);
    const isUpload = source === MEDIA_SOURCE_UPLOAD;
    const fields = document.createElement("div");
    fields.className =
        "vst-detail-fields vst-detail-col vst-detail-instance-fields vst-detail-ref-row vst-detail-ref-editor";
    fields.setAttribute("data-vst-ref-index", `${refIdx}`);

    const select = buildOptionSelect(options, source, (value) => {
        ctx.commit((cs) => {
            const target = cs[clipIdx]?.frameRefs[refIdx];
            if (!target) {
                return;
            }
            const resolved = resolveImageSourceValue(
                value,
                buildImageSourceOptions(value),
            );
            target.source = resolved;
            if (resolved !== MEDIA_SOURCE_UPLOAD) {
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
            "Where this keyframe image comes from — an upload, or another " +
                "clip's rendered frame. The image guides how the clip looks " +
                "at its attach frame.",
        ),
    );

    const frameMax = getReferenceFrameMax(defaults, clip, fps);
    const boundedPositions = endpointPolicy.bounded;
    const supportsFirst = endpointPolicy.supportsFirst;
    const supportsLast = endpointPolicy.supportsLast;
    const currentPositionSupported = ref.fromEnd ? supportsLast : supportsFirst;
    const editableFrameMax =
        endpointPolicy.available && !endpointPolicy.bounded
            ? frameMax
            : REF_FRAME_MIN;
    const frameInput = buildNumber(
        ref.frame,
        REF_FRAME_MIN,
        editableFrameMax,
        1,
        (value) => {
            ctx.debouncedCommit(`ref-${refIdx}-frame`, (cs) => {
                const target = cs[clipIdx]?.frameRefs[refIdx];
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
    frameInput.setAttribute("data-vst-focus-key", `ref-${refIdx}-frame`);
    frameInput.disabled = !hasSupportedEndpoint;
    fields.appendChild(
        buildField(
            "Attach at Frame",
            frameInput,
            undefined,
            boundedReferencePositionHelp(endpointPolicy) ??
                (boundedPositions
                    ? "This clip accepts an image only at a bounded endpoint."
                    : "The frame within the clip where this keyframe is anchored. " +
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
                    const target = cs[clipIdx]?.frameRefs[refIdx];
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
                        const target = cs[clipIdx]?.frameRefs[refIdx];
                        if (target) {
                            target.uploadedImage = { data, fileName };
                            target.uploadFileName = fileName;
                        }
                    });
                    ctx.render();
                },
                () => {
                    ctx.commit((cs) => {
                        const target = cs[clipIdx]?.frameRefs[refIdx];
                        if (target) {
                            target.uploadedImage = null;
                            target.uploadFileName = null;
                        }
                    });
                    ctx.render();
                },
                ref.uploadedImage?.data,
            ),
        );
    }
    return fields;
};
