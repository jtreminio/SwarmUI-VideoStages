import { hasArchitectureSlotSourcedIcLora } from "../architectures/behaviorRegistry";
import {
    AUDIO_SOURCE_UPLOAD,
    buildAudioSourceOptions,
    canUseClipLengthFromAudio,
    isAceStepFunAudioSource,
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
import { getState } from "../persistence";
import type { Clip, TimelineSelection } from "../types";
import { buildAudioTracksPanel } from "./audioTracksPanel";
import {
    applyPersistedCapabilityRepair,
    buildCapabilityNotice,
    buildCapabilityRepairButton,
} from "./capabilityUi";
import type { DetailStripContext } from "./context";

export const buildAudioBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "audio" }>,
    clips: Clip[],
): HTMLElement => {
    const { clipIdx } = sel;
    const clip = clips[clipIdx];
    const capabilityView = ctx.capabilities().forClip(clip);
    const audioDecision = capabilityView.decision("clipAudio");
    const reuseDecision = capabilityView.decision("audioReuse");
    const controlNetEnabled = hasArchitectureSlotSourcedIcLora(
        clip.architecture,
        clip.icLoras,
    );
    const options = buildAudioSourceOptions(clip.audioSource ?? "", {
        controlNetEnabled,
        allowedKinds: capabilityView.audioSourceKinds,
    });
    const source =
        options.find((option) => option.value === clip.audioSource)?.value ??
        clip.audioSource ??
        "";
    const canLength = canUseClipLengthFromAudio(source);
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
        clip.clipLengthFromAudio === true && canLength,
        (value) => {
            commitAudio((c) => {
                c.clipLengthFromAudio = value;
            });
        },
        {
            disabled: !canLength,
            help:
                "Set the clip's duration to match the length of its audio " +
                "instead of a fixed value. Available only for sources with a " +
                "known length.",
        },
    );
    base.appendChild(lengthRow);

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
            repair: {
                label: "Remove unsupported clip audio",
                className: "vst-remove-unsupported-audio",
                onRepair: () => {
                    ctx.structuralCommit((items) => {
                        const target = items[clipIdx];
                        if (!target) {
                            return null;
                        }
                        target.audioSource = "Native";
                        target.uploadedAudio = null;
                        target.reuseAudio = false;
                        target.clipLengthFromAudio = false;
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

    const state = getState();
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
