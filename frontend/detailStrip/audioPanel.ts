import { hasArchitectureSlotSourcedIcLora } from "../architectures/behaviorRegistry";
import {
    AUDIO_SOURCE_UPLOAD,
    buildAudioSourceOptions,
    canUseClipLengthFromAudio,
    defaultAuthoringAudioSource,
    isAceStepFunAudioSource,
    isAllowedAudioSource,
} from "../audioSource";
import {
    buildAccordionSection,
    buildCheckbox,
    buildField,
    buildMediaPickRow,
    buildOptionSelect,
} from "../detailWidgets";
import {
    audioTrackIndicesForClipWindow,
    clipTimelineWindow,
} from "../documentQueries";
import type { Clip, TimelineSelection, VideoStagesConfig } from "../types";
import { buildAudioTracksPanel } from "./audioTracksPanel";
import {
    applyPersistedCapabilityRepair,
    buildCapabilityNotice,
    buildCapabilityRepairButton,
    CAPABILITY_REPAIR_SELECTORS,
} from "./capabilityUi";
import type { DetailStripContext } from "./context";

export const buildAudioBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "audio" }>,
    state: VideoStagesConfig,
): HTMLElement => {
    const clips = state.clips;
    const { clipIdx } = sel;
    const clip = clips[clipIdx];
    const capabilityView = ctx.authoring().capabilities.forClip(clip);
    const audioCapabilityDecision = capabilityView.clipAudio;
    const reuseDecision = capabilityView.decision("audioReuse");
    const durationDecision = capabilityView.decision("audioDerivedDuration");
    // The control signal is IC-LoRA media, so its duration rides on that feature; the reason
    // still names the control the user is looking at.
    const icLoraDecision = capabilityView.decision("icLora");
    const controlDurationDecision = icLoraDecision.supported
        ? icLoraDecision
        : {
              ...icLoraDecision,
              reason: `Control-signal-derived clip duration is not supported by ${capabilityView.architectureLabel}.`,
          };
    const controlSignalEnabled = hasArchitectureSlotSourcedIcLora(
        capabilityView.architectureId,
        clip.icLoras,
    );
    const controlDurationIssueDecision =
        clip.clipLengthFromControlNet && !controlDurationDecision.supported
            ? controlDurationDecision
            : clip.clipLengthFromControlNet && !controlSignalEnabled
              ? {
                    ...controlDurationDecision,
                    supported: false,
                    reason: "No IC-LoRA supplies a ControlNet 1-3 drive source for clip duration.",
                }
              : null;
    const options = buildAudioSourceOptions(clip.audioSource ?? "", {
        controlNetEnabled:
            capabilityView.audioSourceKinds.includes("ControlNet"),
        allowedKinds: capabilityView.audioSourceKinds,
    });
    const source =
        options.find((option) => option.value === clip.audioSource)?.value ??
        clip.audioSource ??
        "";
    const selectedAudioSourceAllowed = isAllowedAudioSource(
        capabilityView.audioSourceKinds,
        source,
    );
    const audioDecision =
        audioCapabilityDecision.supported && !selectedAudioSourceAllowed
            ? {
                  ...audioCapabilityDecision,
                  supported: false,
                  reason: `Audio source '${source}' is not supported by ${capabilityView.architectureLabel}.`,
              }
            : audioCapabilityDecision;
    const canLength = canUseClipLengthFromAudio(source);
    const canDeriveDuration =
        durationDecision.supported && selectedAudioSourceAllowed && canLength;
    const durationIssueDecision =
        selectedAudioSourceAllowed && !canDeriveDuration
            ? durationDecision.supported
                ? {
                      ...durationDecision,
                      supported: false,
                      reason: `Audio source '${source}' cannot determine video duration.`,
                  }
                : durationDecision
            : null;
    const durationUnavailableReason =
        durationIssueDecision?.reason ??
        (!audioDecision.supported ? audioDecision.reason : "");
    const isAce = isAceStepFunAudioSource(source);

    const commitAudio = (mutate: (clip: Clip) => void): void => {
        ctx.commit((cs) => {
            const target = cs[clipIdx];
            if (!target) {
                return;
            }
            mutate(target);
            const nextSource = target.audioSource;
            target.clipLengthFromAudio =
                canUseClipLengthFromAudio(nextSource) &&
                target.clipLengthFromAudio;
            if (target.clipLengthFromAudio) {
                target.clipLengthFromControlNet = false;
            }
            target.saveAudioTrack =
                isAceStepFunAudioSource(nextSource) && target.saveAudioTrack;
            target.uploadedAudio =
                nextSource === AUDIO_SOURCE_UPLOAD
                    ? target.uploadedAudio
                    : null;
        });
    };

    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-audio-body";
    const base = document.createElement("div");
    base.className = "vst-detail-col vst-detail-audio";

    const select = buildOptionSelect(
        options.map((o) => ({ value: o.value, label: o.label })),
        source,
        (value) => {
            commitAudio((c) => {
                c.audioSource = value;
            });
            ctx.render();
        },
    );
    base.appendChild(
        buildField(
            "Audio Source",
            select,
            undefined,
            "Where this clip's audio comes from: generated from the prompt, " +
                "an uploaded file, or a connected generated-audio source.",
        ),
    );

    const reuseRow = buildCheckbox(
        "Reuse Captured Stage Audio",
        clip.reuseAudio === true,
        (value) => {
            commitAudio((c) => {
                c.reuseAudio = value;
            });
        },
        {
            disabled: !reuseDecision.supported,
            help:
                "Capture this clip's audio after its second active stage and " +
                "reuse that captured audio from the third active stage onward. " +
                "Requires at least three active stages." +
                (reuseDecision.reason ? ` ${reuseDecision.reason}` : ""),
        },
    );
    reuseRow.classList.add("vst-detail-audio-reuse");
    base.appendChild(reuseRow);
    if (clip.reuseAudio && !reuseDecision.supported) {
        reuseRow.appendChild(buildCapabilityNotice(reuseDecision));
        reuseRow.appendChild(
            buildCapabilityRepairButton({
                label: "Remove unsupported reuse",
                className: "vst-detail-delete",
                onRepair: () => {
                    commitAudio((target) => {
                        target.reuseAudio = false;
                    });
                    ctx.render();
                },
            }),
        );
    }

    const lengthRow = buildCheckbox(
        "Clip Length from Audio",
        clip.clipLengthFromAudio === true,
        (value) => {
            commitAudio((c) => {
                c.clipLengthFromAudio = value;
            });
        },
        {
            disabled: !canDeriveDuration,
            help:
                "Set the clip's duration to match the length of its audio " +
                "instead of a fixed value. Available only for sources with a " +
                "known length." +
                (durationUnavailableReason
                    ? ` ${durationUnavailableReason}`
                    : ""),
        },
    );
    lengthRow.classList.add("vst-detail-audio-derived-duration");
    base.appendChild(lengthRow);
    if (clip.clipLengthFromAudio && durationIssueDecision) {
        lengthRow.appendChild(buildCapabilityNotice(durationIssueDecision));
        lengthRow.appendChild(
            buildCapabilityRepairButton({
                label: "Remove unsupported audio-derived duration",
                className: "vst-detail-delete",
                onRepair: () => {
                    ctx.commit((items) => {
                        const target = items[clipIdx];
                        if (target) {
                            target.clipLengthFromAudio = false;
                        }
                    });
                    ctx.render();
                },
            }),
        );
    }
    if (clip.clipLengthFromControlNet) {
        const controlLengthStatus = document.createElement("div");
        controlLengthStatus.className =
            "vst-detail-control-signal-derived-duration";
        const status = document.createElement("p");
        status.className = "vst-detail-note";
        status.textContent = "Control-signal-derived clip duration is active.";
        controlLengthStatus.appendChild(status);
        if (controlDurationIssueDecision) {
            controlLengthStatus.appendChild(
                buildCapabilityNotice(controlDurationIssueDecision),
            );
        }
        controlLengthStatus.appendChild(
            buildCapabilityRepairButton({
                label: "Remove control-signal-derived duration",
                className:
                    "vst-detail-delete vst-remove-control-signal-derived-duration",
                onRepair: () => {
                    ctx.commit((items) => {
                        const target = items[clipIdx];
                        if (target) {
                            target.clipLengthFromControlNet = false;
                        }
                    });
                    ctx.render();
                },
            }),
        );
        base.appendChild(controlLengthStatus);
    }

    const saveRow = buildCheckbox(
        "Save Audio Track",
        clip.saveAudioTrack === true && isAce,
        (value) => {
            commitAudio((c) => {
                c.saveAudioTrack = value;
            });
        },
        {
            disabled: !isAce,
            help:
                "Export the generated audio as a separate track alongside the " +
                "video. Only available for generated (AceStep) audio.",
        },
    );
    base.appendChild(saveRow);

    if (source === AUDIO_SOURCE_UPLOAD) {
        base.appendChild(
            buildMediaPickRow(
                "Audio Upload",
                "audio/*",
                ["audio"],
                clip.uploadedAudio?.fileName,
                (data, fileName) => {
                    commitAudio((c) => {
                        c.uploadedAudio = { data, fileName };
                    });
                    ctx.render();
                },
                () => {
                    commitAudio((c) => {
                        c.uploadedAudio = null;
                    });
                    ctx.render();
                },
            ),
        );
    }
    if (!audioDecision.supported) {
        applyPersistedCapabilityRepair(base, audioDecision, {
            keep: [
                ...CAPABILITY_REPAIR_SELECTORS,
                ".vst-detail-audio-reuse",
                ".vst-detail-audio-derived-duration",
                ".vst-detail-control-signal-derived-duration",
            ],
            repair: {
                label: "Remove unsupported clip audio",
                className: "vst-remove-unsupported-audio",
                onRepair: () => {
                    ctx.structuralCommit((items) => {
                        const target = items[clipIdx];
                        if (!target) {
                            return null;
                        }
                        target.audioSource = defaultAuthoringAudioSource(
                            capabilityView.audioSourceKinds,
                        );
                        target.uploadedAudio = null;
                        target.saveAudioTrack = false;
                        return "render";
                    });
                },
            },
        });
    }
    body.appendChild(
        buildAccordionSection({
            key: "base-audio",
            label: "Base Audio",
            content: base,
            open: true,
            flattenContent: true,
        }).section,
    );

    body.appendChild(
        buildAudioTracksPanel(
            ctx,
            state,
            { kind: "none" },
            {
                trackIndices: audioTrackIndicesForClipWindow(state, clipIdx),
                clipWindow:
                    clipTimelineWindow(state.clips, clipIdx) ?? undefined,
            },
        ),
    );

    return body;
};
