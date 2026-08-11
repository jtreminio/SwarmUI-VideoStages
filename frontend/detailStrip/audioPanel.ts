import { hasArchitectureSlotSourcedIcLora } from "../architectures/behaviorRegistry";
import {
    AUDIO_SOURCE_CONTROLNET,
    AUDIO_SOURCE_UPLOAD,
    buildAudioSourceOptions,
    canUseClipLengthFromAudio,
    defaultAuthoringAudioSource,
    isAceStepFunAudioSource,
    isAllowedAudioSource,
} from "../audioSource";
import { runClipMediaProbe } from "../clipMediaProbeGuard";
import { AUDIO_SPAN_MIN_LENGTH } from "../constants";
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
import { probeMediaDurationSeconds } from "../mediaProbe";
import { toInOut } from "../trimGeometry";
import type { AuthoringDocument, Clip, TimelineSelection } from "../types";
import { roundToTenth } from "../utils";
import { buildAudioTracksPanel } from "./audioTracksPanel";
import {
    applyPersistedCapabilityRepair,
    buildCapabilityNotice,
    buildCapabilityRepairButton,
    CAPABILITY_REPAIR_SELECTORS,
} from "./capabilityUi";
import type { DetailStripContext } from "./context";
import { buildSidebarMediaPreview } from "./sidebarMediaPreview";
import { buildTrimLauncher, openTrimModal } from "./trimModal";

export const buildAudioBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "audio" }>,
    state: AuthoringDocument,
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
        controlNetEnabled: capabilityView.audioSourceKinds.includes(
            AUDIO_SOURCE_CONTROLNET,
        ),
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
            if (nextSource !== AUDIO_SOURCE_UPLOAD) {
                target.uploadedAudioDurationSeconds = 0;
                target.uploadedAudioStartSeconds = 0;
                target.uploadedAudioLengthSeconds = 0;
            }
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

    // "Audio track" is the timeline's own entity, edited in its own panel. This is a per-clip
    // output flag, so it borrows the wording its diagnostic already uses instead.
    const saveRow = buildCheckbox(
        "Save audio separately",
        clip.saveAudioTrack === true && isAce,
        (value) => {
            commitAudio((c) => {
                c.saveAudioTrack = value;
            });
        },
        {
            disabled: !isAce,
            help:
                "Export the generated audio as its own output alongside the " +
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
                        c.uploadedAudioDurationSeconds = 0;
                        c.uploadedAudioStartSeconds = 0;
                        c.uploadedAudioLengthSeconds = 0;
                    });
                    ctx.render();
                    if (clip.id) {
                        runClipMediaProbe({
                            clipId: clip.id,
                            slot: "base-audio",
                            probe: () => probeMediaDurationSeconds(data),
                            apply: (target, duration) => {
                                if (target.uploadedAudio?.data !== data) {
                                    return;
                                }
                                target.uploadedAudioDurationSeconds =
                                    roundToTenth(duration);
                            },
                            onApplied: () => ctx.render(),
                        });
                    }
                },
                () => {
                    commitAudio((c) => {
                        c.uploadedAudio = null;
                        c.uploadedAudioDurationSeconds = 0;
                        c.uploadedAudioStartSeconds = 0;
                        c.uploadedAudioLengthSeconds = 0;
                    });
                    ctx.render();
                },
            ),
        );
        if (clip.uploadedAudio) {
            const limitSeconds = clip.uploadedAudioDurationSeconds;
            const shown =
                clip.uploadedAudioLengthSeconds > 0
                    ? {
                          startSeconds: clip.uploadedAudioStartSeconds,
                          lengthSeconds: clip.uploadedAudioLengthSeconds,
                      }
                    : { startSeconds: 0, lengthSeconds: limitSeconds };
            base.appendChild(
                buildSidebarMediaPreview(
                    "audio",
                    clip.uploadedAudio.data,
                    shown,
                ),
            );
            if (limitSeconds > 0) {
                const range = toInOut(shown);
                base.appendChild(
                    buildTrimLauncher(
                        `Range ${range.inSeconds.toFixed(1)}–${range.outSeconds.toFixed(1)} s` +
                            ` · Uses ${shown.lengthSeconds.toFixed(1)} s of ${limitSeconds.toFixed(1)} s`,
                        () =>
                            openTrimModal({
                                mediaKind: "audio",
                                title: "Trim Base Audio",
                                fileName:
                                    clip.uploadedAudio?.fileName ??
                                    "Base audio",
                                dataUri: clip.uploadedAudio?.data ?? null,
                                range: shown,
                                limits: {
                                    limitSeconds,
                                    minLengthSeconds: AUDIO_SPAN_MIN_LENGTH,
                                    fps: 0,
                                },
                                impactText: (next) =>
                                    `Uses ${next.lengthSeconds.toFixed(1)} s of ${limitSeconds.toFixed(1)} s`,
                                onApply: (next) => {
                                    const whole =
                                        next.startSeconds <= 0 &&
                                        next.lengthSeconds >= limitSeconds;
                                    commitAudio((target) => {
                                        target.uploadedAudioStartSeconds = whole
                                            ? 0
                                            : next.startSeconds;
                                        target.uploadedAudioLengthSeconds =
                                            whole ? 0 : next.lengthSeconds;
                                    });
                                    ctx.render();
                                },
                            }),
                    ),
                );
            }
        }
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
