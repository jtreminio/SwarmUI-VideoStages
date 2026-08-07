import { AUDIO_SOURCE_KINDS } from "./architectures/generatedFeatures";
import { getVideoStagesHostBridge } from "./host";
import { preserveSelectedOption, type SelectOption } from "./selectOption";

export type AudioSourceOption = Pick<SelectOption, "value" | "label">;

export interface AudioSourceContext {
    controlNetEnabled?: boolean;
    allowedKinds?: readonly string[];
}

const [
    AUDIO_SOURCE_DISABLED_KIND,
    AUDIO_SOURCE_NATIVE,
    AUDIO_SOURCE_UPLOAD,
    AUDIO_SOURCE_CONTROLNET,
    AUDIO_SOURCE_ACE_STEP_FUN,
] = AUDIO_SOURCE_KINDS;

export {
    AUDIO_SOURCE_ACE_STEP_FUN,
    AUDIO_SOURCE_CONTROLNET,
    AUDIO_SOURCE_DISABLED_KIND,
    AUDIO_SOURCE_NATIVE,
    AUDIO_SOURCE_UPLOAD,
};

const ACESTEPFUN_AUDIO_REF_PATTERN = /^audio(\d+)$/i;

export const isAceStepFunAudioSource = (source: string): boolean =>
    ACESTEPFUN_AUDIO_REF_PATTERN.test(`${source ?? ""}`.trim());

export const audioSourceKind = (source: string): string => {
    const normalized = `${source ?? ""}`.trim() || AUDIO_SOURCE_NATIVE;
    return isAceStepFunAudioSource(normalized)
        ? AUDIO_SOURCE_ACE_STEP_FUN
        : normalized;
};

export const isAllowedAudioSource = (
    allowedKinds: readonly string[],
    source: string,
): boolean => {
    const kind = audioSourceKind(source);
    return (
        allowedKinds.includes(kind) ||
        (kind === AUDIO_SOURCE_NATIVE &&
            allowedKinds.includes(AUDIO_SOURCE_DISABLED_KIND))
    );
};

export const defaultAuthoringAudioSource = (
    allowedKinds: readonly string[],
): string =>
    allowedKinds.includes(AUDIO_SOURCE_NATIVE) ||
    allowedKinds.includes(AUDIO_SOURCE_DISABLED_KIND)
        ? AUDIO_SOURCE_NATIVE
        : (allowedKinds[0] ?? AUDIO_SOURCE_NATIVE);

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
    const snapshot = getVideoStagesHostBridge().getAceStepFunRegistry();
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

export const buildAudioTrackSourceOptions = (
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
    if (context.allowedKinds) {
        const allowed = new Set(context.allowedKinds);
        const filtered = options.filter((option) => {
            const kind = audioSourceKind(option.value);
            return (
                allowed.has(kind) ||
                (kind === AUDIO_SOURCE_NATIVE &&
                    allowed.has(AUDIO_SOURCE_DISABLED_KIND))
            );
        });
        options.length = 0;
        options.push(...filtered);
    }
    appendMissingSelectedRef(options, currentValue);
    preserveSelectedOption(options, currentValue, "start", (value) => ({
        value,
        label: `${value} (unsupported persisted value)`,
    }));
    return options;
};
