import {
    boundaryOverlapChoices,
    normalizeBoundaryOverlap,
} from "../architectures/boundaryConstraints";
import { type BoundaryPlan, crossfadePlanForClips } from "../boundaryPlan";
import {
    activeStageCount,
    executableBoundaryForLeftClip,
    executableClipIndexes,
} from "../clipSemantics";
import {
    buildCheckbox,
    buildField,
    buildOptionSelect,
    buildStaticSection,
    type OptionSpec,
} from "../detailWidgets";
import {
    formatOverlapSeconds,
    formatSecondsTenth,
    safeFps,
} from "../timelineDetail";
import {
    boundaryImpactForLeftClip,
    resolveTimelineTiming,
} from "../timelineTiming";
import { BOUNDARY_GLYPH, BOUNDARY_LABEL } from "../timelineView/regionRenderer";
import type {
    BoundaryOut,
    TimelineSelection,
    VideoStagesConfig,
} from "../types";
import { buildCapabilityNotice } from "./capabilityUi";
import type { DetailStripContext } from "./context";

export const buildBoundaryBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "boundary" }>,
    state: VideoStagesConfig,
): HTMLElement => {
    const clips = state.clips;
    const { leftClipIdx } = sel;
    const body = document.createElement("div");
    body.className = "vst-detail-body";
    const fields = document.createElement("div");
    fields.className = "vst-detail-form-body vst-detail-boundary";
    const clip = clips[leftClipIdx];
    const value: BoundaryOut = clip?.boundaryOut ?? "cut";
    const capabilities = ctx.authoring().capabilities;
    const capability = capabilities.forBoundaryIndex(clips, leftClipIdx);
    const seam = executableBoundaryForLeftClip(clips, leftClipIdx);
    const fps = Math.round(safeFps(state.fps));
    const carryTargetHasStage =
        capability.rightClipIdx !== null &&
        activeStageCount(clips[capability.rightClipIdx]) > 0;
    const carryAudioActive =
        clip?.boundaryOutCarryAudio === true && carryTargetHasStage;

    const joinSpecs: OptionSpec[] = capability.modes.map((mode) => ({
        value: mode,
        label: `${BOUNDARY_LABEL[mode]} ${BOUNDARY_GLYPH[mode]}`,
    }));
    if (!capability.modes.includes(value)) {
        joinSpecs.unshift({
            value,
            label: `${BOUNDARY_LABEL[value]} ${BOUNDARY_GLYPH[value]} (unsupported persisted value)`,
            disabled: true,
        });
    }
    const select = buildOptionSelect(joinSpecs, value, (next) => {
        ctx.commit((cs) => {
            const c = cs[leftClipIdx];
            if (c) {
                c.boundaryOut = (next as BoundaryOut) ?? "cut";
            }
        });
        ctx.render();
    });
    fields.appendChild(
        buildField(
            `Join · Clip ${leftClipIdx} → ${seam === null ? "end" : seam.rightIdx}`,
            select,
            undefined,
            "How this clip joins the next one. Cut: hard concatenation. " +
                "Continue: the next clip is generated from this clip's last " +
                "frames so motion carries through. Crossfade: the overlap is " +
                "dissolved pixel-by-pixel.",
        ),
    );
    if (!capability.modes.includes(value) && capability.reason) {
        fields.appendChild(
            buildCapabilityNotice({
                supported: false,
                reason: capability.reason,
                code: "",
            }),
        );
    }

    const overlapPolicy = capability.overlapConstraints(value);
    if (value !== "cut" && capability.modes.includes(value)) {
        const overlapValue =
            clip?.boundaryOutOverlap ?? overlapPolicy.defaultFrames;
        const overlapSpecs: OptionSpec[] = boundaryOverlapChoices(
            overlapPolicy,
        ).map((frames) => ({
            value: `${frames}`,
            label: `${frames} frames (~${formatOverlapSeconds(frames, fps)})`,
        }));
        if (
            overlapValue > 0 &&
            !overlapSpecs.some((option) => option.value === `${overlapValue}`)
        ) {
            overlapSpecs.unshift({
                value: `${overlapValue}`,
                label: `${overlapValue} frames (unsupported persisted value)`,
                disabled: true,
            });
        }
        const overlapSelect = buildOptionSelect(
            overlapSpecs,
            `${overlapValue}`,
            (next) => {
                ctx.commit((cs) => {
                    const c = cs[leftClipIdx];
                    if (c) {
                        c.boundaryOutOverlap = normalizeBoundaryOverlap(
                            next,
                            overlapPolicy,
                        );
                    }
                });
                ctx.render();
            },
        );
        fields.appendChild(
            buildField(
                "Overlap",
                overlapSelect,
                undefined,
                "How many frames the two clips share at the join. For continue " +
                    "this is the frozen context handed to the next clip; for " +
                    "crossfade it is the length of the dissolve. A clip too " +
                    "short for the overlap falls back to a cut.",
            ),
        );
        fields.appendChild(
            buildCheckbox(
                "Continue outgoing audio into next clip",
                clip?.boundaryOutCarryAudio === true,
                (enabled) => {
                    ctx.commit((cs) => {
                        const c = cs[leftClipIdx];
                        if (c) {
                            c.boundaryOutCarryAudio = enabled;
                        }
                    });
                    ctx.render();
                },
                {
                    disabled: !carryTargetHasStage,
                    help:
                        "Preserve this clip's audio tail at the start of the next clip's LTX generation, " +
                        "then let LTX generate its continuation. The next clip needs an active stage.",
                },
            ),
        );
    }

    // The preview budgets exactly what execution budgets: the compacted
    // executable clips, indexed by this seam's position in that list.
    const executable = executableClipIndexes(clips);
    const plannedWindow = (): number => {
        if (seam === null) {
            return 0;
        }
        const plan: BoundaryPlan = crossfadePlanForClips(
            executable.map((clipIdx) => clips[clipIdx]),
            fps,
            (_left, position, mode) =>
                capabilities
                    .forBoundaryIndex(clips, executable[position])
                    .overlapConstraints(mode),
            (target) => capabilities.forClip(target).frameGrid,
        );
        return plan.fallback ? 0 : (plan.overlaps[seam.position] ?? 0);
    };

    const info = document.createElement("div");
    info.className = "vst-boundary-info";
    const effective = capability.effective(value);
    if (effective !== value) {
        info.classList.add("vst-boundary-warn");
        info.textContent =
            `${BOUNDARY_LABEL[value]} is preserved for repair, but this join executes as ` +
            `${BOUNDARY_LABEL[effective].toLowerCase()}. ${capability.reason}`;
    } else if (value === "cut") {
        info.textContent = "Hard cut — clips are concatenated with no overlap.";
    } else if (value === "continue") {
        const window = plannedWindow();
        if (window <= 0) {
            info.classList.add("vst-boundary-warn");
            info.textContent =
                "This continue will fall back to a cut — a clip is too short for the overlap.";
        } else {
            const requested =
                (clip?.boundaryOutOverlap ?? overlapPolicy.defaultFrames) +
                overlapPolicy.continuityExtraFrames;
            let text =
                `Continue — the next clip is generated with this clip's last ${window} ` +
                `frame${window === 1 ? "" : "s"} (~${formatOverlapSeconds(window, fps)}) as frozen ` +
                "context, and the merge collapses the duplicated frames.";
            if (window < requested) {
                text += " The window was reduced because a clip is too short.";
            }
            if (carryAudioActive) {
                text +=
                    " Its audio tail becomes preserved opening context for the next clip's generated audio.";
            }
            info.textContent = text;
        }
    } else {
        const overlapFrames = plannedWindow();
        if (overlapFrames <= 0) {
            info.classList.add("vst-boundary-warn");
            info.textContent =
                "This crossfade will fall back to a cut — a clip is too short for the overlap window.";
        } else {
            const requested =
                clip?.boundaryOutOverlap ??
                capability.overlapConstraints(value).defaultFrames;
            let text =
                `Crossfade — ${overlapFrames} frame${overlapFrames === 1 ? "" : "s"} ` +
                `(~${formatOverlapSeconds(overlapFrames, fps)}) pixel dissolve.`;
            if (overlapFrames < requested) {
                text += " The window was reduced because a clip is too short.";
            }
            if (carryAudioActive) {
                text +=
                    " Its audio tail becomes preserved opening context for the next clip's generated audio.";
            }
            info.textContent = text;
        }
    }
    fields.appendChild(info);

    if (seam !== null) {
        const timing = resolveTimelineTiming(clips, fps, capabilities);
        const impact = boundaryImpactForLeftClip(timing, leftClipIdx);
        if (impact) {
            const leftFrames = timing.clipFrames[impact.leftIdx] ?? 0;
            const rightFrames = timing.clipFrames[impact.rightIdx] ?? 0;
            const combinedFrames = Math.max(
                0,
                leftFrames + rightFrames - impact.overlapFrames,
            );
            const impactBlock = document.createElement("div");
            impactBlock.className = "vst-boundary-impact";

            const heading = document.createElement("div");
            heading.className =
                "vst-detail-crumb vst-detail-subsection-crumb vst-boundary-impact-title";
            heading.textContent = "Output impact";
            impactBlock.appendChild(heading);

            const rows = document.createElement("div");
            rows.className = "vst-boundary-impact-rows";
            const addRow = (
                label: string,
                frames: number,
                sign: "" | "+" | "−" = "",
                strong = false,
            ): void => {
                const row = document.createElement("div");
                row.className = `vst-boundary-impact-row${strong ? " vst-boundary-impact-total" : ""}`;
                const name = document.createElement("span");
                name.textContent = label;
                const value = document.createElement("span");
                value.textContent = `${sign}${frames}f · ${sign}${formatSecondsTenth(frames / fps)}`;
                row.append(name, value);
                rows.appendChild(row);
            };
            addRow(`Clip ${impact.leftIdx}`, leftFrames);
            addRow(`Clip ${impact.rightIdx}`, rightFrames, "+");
            addRow(
                `${BOUNDARY_LABEL[impact.effectiveMode]} shared`,
                impact.overlapFrames,
                impact.overlapFrames > 0 ? "−" : "",
            );
            addRow("Pair after this join", combinedFrames, "", true);
            impactBlock.appendChild(rows);

            if (
                value === "continue" &&
                impact.overlapFrames > 0 &&
                overlapPolicy.continuityExtraFrames > 0
            ) {
                const note = document.createElement("div");
                note.className = "vst-boundary-impact-note";
                const selectedFrames = Math.max(
                    0,
                    impact.overlapFrames - overlapPolicy.continuityExtraFrames,
                );
                note.textContent =
                    `${selectedFrames}f selected + ` +
                    `${overlapPolicy.continuityExtraFrames} LTX continuation frame = ` +
                    `${impact.overlapFrames}f effective shared window.`;
                impactBlock.appendChild(note);
            }
            fields.appendChild(impactBlock);
        }
    }

    body.appendChild(
        buildStaticSection({
            key: "boundary",
            label: "Boundary",
            className: "vst-detail-boundary-section",
            content: fields,
            flattenContent: true,
        }).section,
    );

    return body;
};
