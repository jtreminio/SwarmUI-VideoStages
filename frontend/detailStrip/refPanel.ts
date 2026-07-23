import { clamp, mediaPreviewSrc, REF_FRAME_MIN } from "../constants";
import {
    buildCheckbox,
    buildField,
    buildInstanceRow,
    buildMediaPickRow,
    buildNumber,
    buildOptionSelect,
    wrapForm,
} from "../detailWidgets";
import {
    buildImageSourceOptions,
    resolveImageSourceValue,
} from "../imageSource";
import { getReferenceFrameMax } from "../normalization";
import { getState } from "../persistence";
import { getRootDefaults } from "../rootDefaults";
import { setSelection } from "../selection";
import { type Clip, REF_SOURCE_UPLOAD, type TimelineSelection } from "../types";
import { disableCapabilityControls } from "./capabilityUi";
import type { DetailStripContext } from "./context";

const GROUP_REF = "vstdock_ref";

/**
 * The ref panel lists EVERY reference of the clip, stacked. The selected ref
 * is highlighted; touching any ref's control re-points the selection to it
 * and per-ref keys keep edits distinct.
 */
export const buildRefBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "ref" }>,
    clips: Clip[],
): HTMLElement => {
    const { clipIdx } = sel;
    const clip = clips[clipIdx];
    const body = document.createElement("div");
    body.className =
        "vst-detail-form-body vst-detail-instance-body vst-detail-ref-body";
    const frameMax = getReferenceFrameMax(
        getRootDefaults,
        clip,
        getState().fps,
    );

    clip.refs.forEach((ref, refIdx) => {
        const options = buildImageSourceOptions(ref.source ?? "");
        const source = resolveImageSourceValue(ref.source ?? "", options);
        const isUpload = source === REF_SOURCE_UPLOAD;
        const { row, fields } = buildInstanceRow({
            rowClass: "vst-detail-ref-row",
            indexAttr: "data-vst-ref-index",
            index: refIdx,
            active: refIdx === sel.refIdx,
            title: `R${refIdx + 1}`,
            deleteLabel: "Delete",
            onDelete: () => ctx.deleteRefEntry(clipIdx, refIdx),
            repoint: () => setSelection({ kind: "ref", clipIdx, refIdx }),
        });

        const select = buildOptionSelect(options, source, (value) => {
            ctx.commit((cs) => {
                const r = cs[clipIdx]?.refs[refIdx];
                if (!r) {
                    return;
                }
                const resolved = resolveImageSourceValue(
                    value,
                    buildImageSourceOptions(value),
                );
                r.source = resolved;
                if (resolved !== REF_SOURCE_UPLOAD) {
                    r.uploadedImage = null;
                    r.uploadFileName = null;
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

        const frameInput = buildNumber(
            ref.frame,
            REF_FRAME_MIN,
            frameMax,
            1,
            (value) => {
                ctx.debouncedCommit(`ref-${refIdx}-frame`, (cs) => {
                    const r = cs[clipIdx]?.refs[refIdx];
                    if (r) {
                        r.frame = clamp(
                            Math.round(value),
                            REF_FRAME_MIN,
                            frameMax,
                        );
                    }
                });
            },
        );
        frameInput.setAttribute("data-vst-focus-key", `ref-${refIdx}-frame`);
        fields.appendChild(
            buildField(
                "Attach at Frame",
                frameInput,
                undefined,
                "The frame within the clip where this reference is anchored. " +
                    "Frame 1 is the first frame; the image influences the clip " +
                    "most strongly around here.",
            ),
        );

        fields.appendChild(
            buildCheckbox(
                "Count from clip end",
                ref.fromEnd === true,
                (value) => {
                    ctx.commit((cs) => {
                        const r = cs[clipIdx]?.refs[refIdx];
                        if (r) {
                            r.fromEnd = value;
                        }
                    });
                },
                {
                    help:
                        "Count the attach frame backwards from the last frame " +
                        "instead of forward from the first — so it stays " +
                        "anchored to the end even if the clip length changes.",
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
                            const r = cs[clipIdx]?.refs[refIdx];
                            if (r) {
                                r.uploadedImage = { data, fileName };
                                r.uploadFileName = fileName;
                            }
                        });
                        ctx.render();
                    },
                    () => {
                        ctx.commit((cs) => {
                            const r = cs[clipIdx]?.refs[refIdx];
                            if (r) {
                                r.uploadedImage = null;
                                r.uploadFileName = null;
                            }
                        });
                        ctx.render();
                    },
                ),
            );
        }
        const decision = ctx
            .capabilities()
            .forClip(clip)
            .decision("frameReferences");
        if (!decision.supported) {
            disableCapabilityControls(row, decision, [".vst-detail-delete"]);
        }
        body.appendChild(row);
    });

    return wrapForm(GROUP_REF, body);
};
