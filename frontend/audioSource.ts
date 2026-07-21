import {
    preserveSelectedOption,
    resolveSelectValue,
    type SelectOption,
} from "./selectOption";

export type AudioSourceOption = Pick<SelectOption, "value" | "label">;

export interface AudioSourceContext {
    controlNetEnabled?: boolean;
}

export const AUDIO_SOURCE_NATIVE = "Native";
export const AUDIO_SOURCE_UPLOAD = "Upload";
export const AUDIO_SOURCE_CONTROLNET = "ControlNet";
export const AUDIO_SOURCE_VOICE_REF = "Voice Reference";
const ACESTEPFUN_AUDIO_REF_PATTERN = /^audio(\d+)$/i;

export const isAceStepFunAudioSource = (source: string): boolean =>
    ACESTEPFUN_AUDIO_REF_PATTERN.test(`${source ?? ""}`.trim());

export const isControlNetAudioSource = (source: string): boolean =>
    `${source ?? ""}`.trim() === AUDIO_SOURCE_CONTROLNET;

export const canUseClipLengthFromAudio = (source: string): boolean => {
    const normalized = `${source ?? ""}`.trim();
    return (
        normalized === AUDIO_SOURCE_UPLOAD ||
        isAceStepFunAudioSource(normalized) ||
        isControlNetAudioSource(normalized)
    );
};

const getAceStepFunRefs = (): string[] => {
    const snapshot = window.acestepfunTrackRegistry?.getSnapshot?.();
    if (!snapshot?.enabled || !Array.isArray(snapshot.refs)) {
        return [];
    }
    const seen = new Set<string>();
    const refs: string[] = [];
    for (const raw of snapshot.refs) {
        const ref = `${raw || ""}`.trim();
        if (!ref || seen.has(ref)) {
            continue;
        }
        seen.add(ref);
        refs.push(ref);
    }
    return refs;
};

const getAceStepFunRefLabel = (ref: string): string => {
    const audioRef = ACESTEPFUN_AUDIO_REF_PATTERN.exec(ref);
    if (audioRef) {
        return `AceStepFun Audio ${audioRef[1]}`;
    }
    return ref;
};

const appendAceStepFunRefs = (options: AudioSourceOption[]): void => {
    for (const ref of getAceStepFunRefs()) {
        options.push({ value: ref, label: getAceStepFunRefLabel(ref) });
    }
};

const appendMissingSelectedRef = (
    options: AudioSourceOption[],
    currentValue: string,
): void =>
    preserveSelectedOption(options, currentValue, "end", (value) =>
        isAceStepFunAudioSource(value)
            ? { value, label: getAceStepFunRefLabel(value) }
            : null,
    );

export const buildSegmentAudioSourceOptions = (
    currentValue = "",
): AudioSourceOption[] => {
    const options: AudioSourceOption[] = [
        { value: AUDIO_SOURCE_UPLOAD, label: AUDIO_SOURCE_UPLOAD },
    ];
    appendAceStepFunRefs(options);
    appendMissingSelectedRef(options, currentValue);
    return options;
};

export const buildAudioSourceOptions = (
    currentValue = "",
    context: AudioSourceContext = {},
): AudioSourceOption[] => {
    const options: AudioSourceOption[] = [
        { value: AUDIO_SOURCE_NATIVE, label: AUDIO_SOURCE_NATIVE },
        { value: AUDIO_SOURCE_UPLOAD, label: AUDIO_SOURCE_UPLOAD },
        { value: AUDIO_SOURCE_VOICE_REF, label: AUDIO_SOURCE_VOICE_REF },
    ];
    appendAceStepFunRefs(options);
    if (context.controlNetEnabled) {
        options.push({
            value: AUDIO_SOURCE_CONTROLNET,
            label: AUDIO_SOURCE_CONTROLNET,
        });
    }
    appendMissingSelectedRef(options, currentValue);
    return options;
};

export const resolveAudioSourceValue = (
    currentValue: string,
    options: AudioSourceOption[],
): string => resolveSelectValue(currentValue, options, AUDIO_SOURCE_NATIVE);
