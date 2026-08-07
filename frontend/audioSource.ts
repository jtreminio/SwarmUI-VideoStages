import { AUDIO_SOURCE_KINDS } from "./architectures/generatedFeatures";
import { getVideoStagesHostBridge } from "./host";
import { equalsMediaSource, parseAceStepFunIndex } from "./mediaSourceSyntax";
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

export const isAceStepFunAudioSource = (source: string): boolean =>
    parseAceStepFunIndex(source) !== null;

/** The spellings AudioSource.Parse matches literally, in its order. */
const LITERAL_AUDIO_SOURCES = [
    AUDIO_SOURCE_NATIVE,
    AUDIO_SOURCE_UPLOAD,
    AUDIO_SOURCE_CONTROLNET,
];

/**
 * The document's own source string, cased the way the backend spells it. The backend compares
 * through StringUtils.Equals, so a differently-cased spelling names the same source there;
 * normalization heals it here so every exact compare downstream — option lookup, the audio
 * panel's Upload check — agrees with the capability layer. An indexed AceStepFun ref keeps its
 * authored spelling: it carries a track number, not a kind.
 */
export const canonicalAudioSource = (source: string): string => {
    const normalized = `${source ?? ""}`.trim();
    if (!normalized) {
        return AUDIO_SOURCE_NATIVE;
    }
    return (
        LITERAL_AUDIO_SOURCES.find((kind) =>
            equalsMediaSource(kind, normalized),
        ) ?? normalized
    );
};

/** Mirrors AudioSource.Parse: an unrecognized value stands in for AudioSourceKind.Unknown. */
export const audioSourceKind = (source: string): string => {
    const canonical = canonicalAudioSource(source);
    return isAceStepFunAudioSource(canonical)
        ? AUDIO_SOURCE_ACE_STEP_FUN
        : canonical;
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
    audioSourceKind(source) === AUDIO_SOURCE_CONTROLNET;

/** Mirrors AudioSourceKindPolicy.CanDriveClipDuration. */
export const canUseClipLengthFromAudio = (source: string): boolean => {
    const kind = audioSourceKind(source);
    return (
        kind === AUDIO_SOURCE_UPLOAD ||
        kind === AUDIO_SOURCE_CONTROLNET ||
        kind === AUDIO_SOURCE_ACE_STEP_FUN
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
    const index = parseAceStepFunIndex(ref);
    return index === null ? ref : `AceStepFun Audio ${index}`;
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
