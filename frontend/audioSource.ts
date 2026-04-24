export interface AudioSourceOption {
    value: string;
    label: string;
}

export const AUDIO_SOURCE_NATIVE = "Native";
export const AUDIO_SOURCE_UPLOAD = "Upload";

const ACESTEPFUN_EVENT = "acestepfun:tracks-changed";
const SOURCE_SELECT_SELECTOR = '[data-clip-field="audioSource"]';
const ACESTEPFUN_AUDIO_REF_PATTERN = /^audio(\d+)$/i;

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

export const buildAudioSourceOptions = (): AudioSourceOption[] => {
    const options: AudioSourceOption[] = [
        { value: AUDIO_SOURCE_NATIVE, label: AUDIO_SOURCE_NATIVE },
        { value: AUDIO_SOURCE_UPLOAD, label: AUDIO_SOURCE_UPLOAD },
    ];
    for (const ref of getAceStepFunRefs()) {
        options.push({ value: ref, label: getAceStepFunRefLabel(ref) });
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

    const runOnEachBuild = (): void => {
        try {
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
        },
    };
};
