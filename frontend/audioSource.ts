export interface AudioSourceOption {
    value: string;
    label: string;
}

export interface AudioSourceContext {
    /**
     * True when the clip's ControlNet source dropdown is enabled (i.e. a
     * controlNetLora is selected). Drives whether "ControlNet" is offered
     * as an audio source.
     */
    controlNetEnabled?: boolean;
}

export const AUDIO_SOURCE_NATIVE = "Native";
export const AUDIO_SOURCE_UPLOAD = "Upload";
export const AUDIO_SOURCE_CONTROLNET = "ControlNet";

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

/** Appends one option per AceStepFun generated track in the registry. */
const appendAceStepFunRefs = (options: AudioSourceOption[]): void => {
    for (const ref of getAceStepFunRefs()) {
        options.push({ value: ref, label: getAceStepFunRefLabel(ref) });
    }
};

/**
 * Keeps a still-selected AceStepFun ref that is no longer in the registry so
 * the select doesn't silently drop it. Always the last option.
 */
const appendMissingSelectedRef = (
    options: AudioSourceOption[],
    currentValue: string,
): void => {
    const selected = `${currentValue || ""}`.trim();
    if (
        isAceStepFunAudioSource(selected) &&
        !options.some((option) => option.value === selected)
    ) {
        options.push({
            value: selected,
            label: getAceStepFunRefLabel(selected),
        });
    }
};

/**
 * Options for an audio SEGMENT's source select: an upload, or any AceStepFun
 * generated track ("audio0", …). Unlike the clip-level options there is no
 * Native/ControlNet — segments are overlay pieces with their own audio.
 */
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
): string => {
    const desired = `${currentValue || ""}`;
    if (options.some((option) => option.value === desired)) {
        return desired;
    }
    return AUDIO_SOURCE_NATIVE;
};
