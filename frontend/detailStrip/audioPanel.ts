import {
    AUDIO_SOURCE_UPLOAD,
    AUDIO_SOURCE_VOICE_REF,
    buildAudioSourceOptions,
    canUseClipLengthFromAudio,
    isAceStepFunAudioSource,
    resolveAudioSourceValue,
} from "../audioSource";
import {
    buildCheckbox,
    buildField,
    buildOptionSelect,
    buildUploadRow,
    wrapForm,
} from "../detailWidgets";
import { hasSlotSourcedIcLora } from "../normalization";
import type { Clip, TimelineSelection } from "../types";
import type { DetailStripContext } from "./context";

const GROUP_AUDIO = "vstdock_audio";

export const buildAudioBody = (
    ctx: DetailStripContext,
    sel: Extract<TimelineSelection, { kind: "audio" }>,
    clips: Clip[],
): HTMLElement => {
    const { clipIdx } = sel;
    const clip = clips[clipIdx];
    const controlNetEnabled = hasSlotSourcedIcLora(clip.icLoras);
    const options = buildAudioSourceOptions(clip.audioSource ?? "", {
        controlNetEnabled,
    });
    const source = resolveAudioSourceValue(clip.audioSource ?? "", options);
    const canLength = canUseClipLengthFromAudio(source);
    const isAce = isAceStepFunAudioSource(source);

    const commitAudio = (mutate: (clip: Clip) => void): void => {
        ctx.commit((cs) => {
            const target = cs[clipIdx];
            if (!target) {
                return;
            }
            mutate(target);
            const cnEnabled = hasSlotSourcedIcLora(target.icLoras);
            const nextSource = resolveAudioSourceValue(
                target.audioSource,
                buildAudioSourceOptions(target.audioSource, {
                    controlNetEnabled: cnEnabled,
                }),
            );
            target.audioSource = nextSource;
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
    body.appendChild(buildField("Audio Source", select));

    body.appendChild(
        buildCheckbox("Reuse Audio", clip.reuseAudio === true, (value) => {
            commitAudio((c) => {
                c.reuseAudio = value;
            });
        }),
    );

    const lengthRow = buildCheckbox(
        "Clip Length from Audio",
        clip.clipLengthFromAudio === true && canLength,
        (value) => {
            commitAudio((c) => {
                c.clipLengthFromAudio = value;
            });
        },
        { disabled: !canLength },
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
        { disabled: !isAce },
    );
    body.appendChild(saveRow);

    if (source === AUDIO_SOURCE_UPLOAD || source === AUDIO_SOURCE_VOICE_REF) {
        body.appendChild(
            buildUploadRow(
                source === AUDIO_SOURCE_VOICE_REF
                    ? "Voice Sample"
                    : "Audio Upload",
                "audio/*",
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
    const addSegment = document.createElement("button");
    addSegment.type = "button";
    addSegment.className = "basic-button small-button vst-detail-add-segment";
    addSegment.textContent = "+ Add segment";
    addSegment.title =
        "Overlay an extra uploaded audio piece on this clip's audio lane";
    addSegment.addEventListener("click", (event) => {
        event.preventDefault();
        ctx.addAudioSegment(clipIdx);
    });
    body.appendChild(addSegment);
    if (segCount > 0) {
        const note = document.createElement("p");
        note.className = "vst-detail-note";
        note.textContent =
            segCount === 1
                ? "1 overlay segment · mixed additively over the base audio."
                : `${segCount} overlay segments · mixed additively over the base audio.`;
        body.appendChild(note);
    }
    return wrapForm(GROUP_AUDIO, body);
};
