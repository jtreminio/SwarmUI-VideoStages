import { VideoStageUtils } from "./UtilsTemp";

export interface AudioSourceOption {
    value: string;
    label: string;
}

export const AUDIO_SOURCE_NATIVE = "Native";
export const AUDIO_SOURCE_UPLOAD = "Upload";
export const AUDIO_SOURCE_SWARM = "Swarm Audio";

const TEXT2AUDIO_TOGGLE_ID = "input_group_content_texttoaudio_toggle";
const ACESTEPFUN_EVENT = "acestepfun:tracks-changed";
const SOURCE_SELECT_SELECTOR = '[data-clip-field="audioSource"]';

const getSourceSelects = (): HTMLSelectElement[] =>
    Array.from(document.querySelectorAll(SOURCE_SELECT_SELECTOR)).filter(
        (elem): elem is HTMLSelectElement => elem instanceof HTMLSelectElement,
    );

const isSourceSelect = (
    target: EventTarget | null,
): target is HTMLSelectElement =>
    target instanceof HTMLSelectElement &&
    target.matches(SOURCE_SELECT_SELECTOR);

const isTextToAudioEnabled = (): boolean => {
    const toggle = VideoStageUtils.getInputElement(TEXT2AUDIO_TOGGLE_ID);
    return !!toggle?.checked;
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

export const buildAudioSourceOptions = (): AudioSourceOption[] => {
    const options: AudioSourceOption[] = [
        { value: AUDIO_SOURCE_NATIVE, label: AUDIO_SOURCE_NATIVE },
        { value: AUDIO_SOURCE_UPLOAD, label: AUDIO_SOURCE_UPLOAD },
    ];
    if (isTextToAudioEnabled()) {
        options.push({
            value: AUDIO_SOURCE_SWARM,
            label: AUDIO_SOURCE_SWARM,
        });
    }
    for (const ref of getAceStepFunRefs()) {
        options.push({ value: ref, label: ref });
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

export const audioSource = () => {
    const refreshOptions = (): void => {
        const selects = getSourceSelects();
        if (selects.length === 0) {
            return;
        }
        const options = buildAudioSourceOptions();
        for (const select of selects) {
            const desired = resolveAudioSourceValue(select.value, options);
            const newValuesJson = JSON.stringify(options.map((o) => o.value));
            const currentValuesJson = JSON.stringify(
                Array.from(select.options).map((o) => o.value),
            );
            if (
                newValuesJson === currentValuesJson &&
                select.value === desired
            ) {
                continue;
            }
            select.innerHTML = "";
            for (const option of options) {
                const elem = document.createElement("option");
                elem.value = option.value;
                elem.textContent = option.label;
                elem.selected = option.value === desired;
                select.appendChild(elem);
            }
            triggerChangeFor(select);
        }
    };

    const onDocumentDropdownInteraction = (event: Event): void => {
        if (isSourceSelect(event.target)) {
            refreshOptions();
        }
    };

    let lastBoundText2AudioToggle: HTMLInputElement | null = null;
    const bindText2AudioToggle = (): void => {
        const toggle = VideoStageUtils.getInputElement(TEXT2AUDIO_TOGGLE_ID);
        if (!toggle || toggle === lastBoundText2AudioToggle) {
            return;
        }
        toggle.addEventListener("change", refreshOptions);
        lastBoundText2AudioToggle = toggle;
    };

    const runOnEachBuild = (): void => {
        try {
            bindText2AudioToggle();
            refreshOptions();
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
    document.addEventListener(ACESTEPFUN_EVENT, refreshOptions);

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
            document.removeEventListener(ACESTEPFUN_EVENT, refreshOptions);
            if (lastBoundText2AudioToggle) {
                lastBoundText2AudioToggle.removeEventListener(
                    "change",
                    refreshOptions,
                );
                lastBoundText2AudioToggle = null;
            }
        },
    };
};
