import { hasArchitectureSlotSourcedIcLora } from "../architectures/behaviorRegistry";
import {
    AUDIO_SOURCE_UPLOAD,
    AUDIO_SOURCE_VOICE_REF,
    buildAudioSourceOptions,
    canUseClipLengthFromAudio,
    isAceStepFunAudioSource,
} from "../audioSource";
import {
    buildCheckbox,
    buildField,
    buildMediaPickRow,
    buildOptionSelect,
    wrapForm,
} from "../detailWidgets";
import type { Clip, TimelineSelection } from "../types";
import {
    buildCapabilityNotice,
    disableCapabilityControls,
} from "./capabilityUi";
import type { DetailStripContext } from "./context";

const GROUP_AUDIO = "vstdock_audio";

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
    const segmentDecision = capabilityView.decision("audioSegments");
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
                nextSource === AUDIO_SOURCE_UPLOAD ||
                nextSource === AUDIO_SOURCE_VOICE_REF
                    ? target.uploadedAudio
                    : null;
        });
    };

    const body = document.createElement("div");
    body.className = "vst-detail-form-body";

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
    body.appendChild(
        buildField(
            "Audio Source",
            select,
            undefined,
            "Where this clip's audio comes from: generated from the prompt, " +
                "an uploaded file, a Voice Reference (clone a speaker sample), " +
                "or none.",
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
    body.appendChild(reuseRow);
    if (clip.reuseAudio && !reuseDecision.supported) {
        reuseRow.appendChild(buildCapabilityNotice(reuseDecision));
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "basic-button small-button vst-detail-delete";
        remove.textContent = "Remove unsupported reuse";
        remove.addEventListener("click", () => {
            commitAudio((target) => {
                target.reuseAudio = false;
            });
            ctx.render();
        });
        reuseRow.appendChild(remove);
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
    body.appendChild(lengthRow);

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
    body.appendChild(saveRow);

    if (source === AUDIO_SOURCE_UPLOAD || source === AUDIO_SOURCE_VOICE_REF) {
        body.appendChild(
            buildMediaPickRow(
                source === AUDIO_SOURCE_VOICE_REF
                    ? "Voice Sample"
                    : "Audio Upload",
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
    if (source === AUDIO_SOURCE_VOICE_REF) {
        const hint = document.createElement("small");
        hint.className = "vst-audio-field-hint";
        hint.textContent =
            "Speaker sample only — new speech is generated to match the " +
            "prompt in this voice. Put the spoken words in the clip prompt.";
        body.appendChild(hint);
    }

    const segCount = clip.audioSegments?.length ?? 0;
    if (segmentDecision.supported) {
        const addSegment = document.createElement("button");
        addSegment.type = "button";
        addSegment.className =
            "basic-button small-button vst-detail-add-segment";
        addSegment.textContent = "+ Add segment";
        addSegment.title =
            "Overlay an extra uploaded audio piece on this clip's audio lane";
        addSegment.addEventListener("click", (event) => {
            event.preventDefault();
            ctx.addAudioSegment(clipIdx);
        });
        body.appendChild(addSegment);
    } else if (segCount > 0) {
        body.appendChild(buildCapabilityNotice(segmentDecision));
    }
    if (segCount > 0) {
        const note = document.createElement("p");
        note.className = "vst-detail-note";
        note.textContent =
            segCount === 1
                ? "1 overlay segment · mixed additively over the base audio."
                : `${segCount} overlay segments · mixed additively over the base audio.`;
        body.appendChild(note);
    }
    if (!audioDecision.supported) {
        disableCapabilityControls(body, audioDecision, [
            ".vst-remove-unsupported-audio",
            ".vst-detail-add-segment",
        ]);
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className =
            "basic-button small-button vst-remove-unsupported-audio";
        remove.textContent = "Remove unsupported clip audio";
        remove.addEventListener("click", () => {
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
        });
        body.appendChild(remove);
    }
    return wrapForm(GROUP_AUDIO, body);
};
