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

import { videoStagesDebugLog } from "./debugLog";

const ACESTEPFUN_EVENT = "acestepfun:tracks-changed";
const SOURCE_SELECT_SELECTOR = '[data-clip-field="audioSource"]';
const CONTROLNET_SOURCE_SELECT_SELECTOR =
    '[data-clip-field="controlNetSource"]';
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

const getSourceSelects = (): HTMLSelectElement[] =>
    Array.from(document.querySelectorAll(SOURCE_SELECT_SELECTOR)).filter(
        (elem): elem is HTMLSelectElement => elem instanceof HTMLSelectElement,
    );

const isSourceSelect = (
    target: EventTarget | null,
): target is HTMLSelectElement =>
    target instanceof HTMLSelectElement &&
    target.matches(SOURCE_SELECT_SELECTOR);

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

export const buildAudioSourceOptions = (
    currentValue = "",
    context: AudioSourceContext = {},
): AudioSourceOption[] => {
    const options: AudioSourceOption[] = [
        { value: AUDIO_SOURCE_NATIVE, label: AUDIO_SOURCE_NATIVE },
        { value: AUDIO_SOURCE_UPLOAD, label: AUDIO_SOURCE_UPLOAD },
    ];
    for (const ref of getAceStepFunRefs()) {
        options.push({ value: ref, label: getAceStepFunRefLabel(ref) });
    }
    if (context.controlNetEnabled) {
        options.push({
            value: AUDIO_SOURCE_CONTROLNET,
            label: AUDIO_SOURCE_CONTROLNET,
        });
    }
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

const detectControlNetEnabledForAudioSelect = (
    audioSelect: HTMLSelectElement,
): boolean => {
    const clipIdx = audioSelect.dataset.clipIdx;
    if (!clipIdx) {
        return false;
    }
    for (const elem of document.querySelectorAll(
        CONTROLNET_SOURCE_SELECT_SELECTOR,
    )) {
        if (
            elem instanceof HTMLSelectElement &&
            elem.dataset.clipIdx === clipIdx
        ) {
            return !elem.disabled;
        }
    }
    return false;
};

export const audioSource = () => {
    const refreshOptions = (reason = "manual"): void => {
        const selects = getSourceSelects();
        videoStagesDebugLog("audioSource", "refreshOptions", {
            reason,
            selectCount: selects.length,
        });
        if (selects.length === 0) {
            return;
        }
        for (const select of selects) {
            const options = buildAudioSourceOptions(select.value, {
                controlNetEnabled:
                    detectControlNetEnabledForAudioSelect(select),
            });
            const desired = resolveAudioSourceValue(select.value, options);
            const newOptionsJson = JSON.stringify(
                options.map((o) => [o.value, o.label]),
            );
            const currentOptionsJson = JSON.stringify(
                Array.from(select.options).map((o) => [
                    o.value,
                    o.textContent ?? "",
                ]),
            );
            if (
                newOptionsJson === currentOptionsJson &&
                select.value === desired
            ) {
                continue;
            }
            videoStagesDebugLog("audioSource", "refreshOptions DOM rebuild", {
                reason,
                previousValue: select.value,
                desired,
            });
            select.innerHTML = "";
            for (const option of options) {
                const elem = document.createElement("option");
                elem.value = option.value;
                elem.textContent = option.label;
                elem.dataset.cleanname = option.label;
                elem.selected = option.value === desired;
                select.appendChild(elem);
            }
            triggerChangeFor(select);
        }
    };

    const onDocumentDropdownInteraction = (event: Event): void => {
        if (isSourceSelect(event.target)) {
            refreshOptions("dropdown-interaction");
        }
    };

    const onAceStepFunTracksChanged = (): void => {
        refreshOptions("acestepfun:tracks-changed");
    };

    const runOnEachBuild = (): void => {
        try {
            refreshOptions("postParamBuildSteps");
        } catch (error) {
            console.warn("audioSource: param build sync failed", error);
        }
    };

    const scheduleInitialSync = (): void => {
        if (!Array.isArray(postParamBuildSteps)) {
            setTimeout(scheduleInitialSync, 200);
            return;
        }
        postParamBuildSteps.push(runOnEachBuild);
    };

    document.addEventListener("mousedown", onDocumentDropdownInteraction);
    document.addEventListener("focusin", onDocumentDropdownInteraction);
    document.addEventListener(ACESTEPFUN_EVENT, onAceStepFunTracksChanged);

    scheduleInitialSync();

    return {
        buildOptions: buildAudioSourceOptions,
        resolveSelectedValue: resolveAudioSourceValue,
        refreshOptions,
        runOnEachBuild,
        dispose: (): void => {
            document.removeEventListener(
                "mousedown",
                onDocumentDropdownInteraction,
            );
            document.removeEventListener(
                "focusin",
                onDocumentDropdownInteraction,
            );
            document.removeEventListener(
                ACESTEPFUN_EVENT,
                onAceStepFunTracksChanged,
            );
        },
    };
};
