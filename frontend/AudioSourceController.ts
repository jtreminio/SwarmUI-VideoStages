import { VideoStageUtils } from "./Utils";

interface AudioSourceOption {
    value: string;
    label: string;
}

export const AudioSourceController = () => {
    const NATIVE_VALUE = "Native";
    const UPLOAD_VALUE = "Upload";
    const SWARM_VALUE = "Swarm Audio";
    const SOURCE_INPUT_ID = "input_vsaudiosource";
    const UPLOAD_INPUT_ID = "input_vsaudioupload";
    const TEXT2AUDIO_TOGGLE_ID = "input_group_content_texttoaudio_toggle";
    const ACESTEPFUN_EVENT = "acestepfun:tracks-changed";

    const getSourceSelect = (): HTMLSelectElement | null =>
        VideoStageUtils.getSelectElement(SOURCE_INPUT_ID);

    const getUploadContainer = (): HTMLElement | null => {
        const fileInput = VideoStageUtils.getInputElement(UPLOAD_INPUT_ID);
        if (!fileInput) {
            return null;
        }
        return findParentOfClass(fileInput, "auto-input");
    };

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

    const buildOptions = (): AudioSourceOption[] => {
        const options: AudioSourceOption[] = [
            { value: NATIVE_VALUE, label: NATIVE_VALUE },
            { value: UPLOAD_VALUE, label: UPLOAD_VALUE },
        ];
        if (isTextToAudioEnabled()) {
            options.push({ value: SWARM_VALUE, label: SWARM_VALUE });
        }
        for (const ref of getAceStepFunRefs()) {
            options.push({ value: ref, label: ref });
        }
        return options;
    };

    const resolveSelectedValue = (
        currentValue: string,
        options: AudioSourceOption[],
    ): string => {
        const desired = `${currentValue || ""}`;
        if (options.some((o) => o.value === desired)) {
            return desired;
        }
        return NATIVE_VALUE;
    };

    const applyUploadVisibility = (): void => {
        const container = getUploadContainer();
        if (!container) {
            return;
        }
        const select = getSourceSelect();
        const showUpload = !!select && `${select.value || ""}` === UPLOAD_VALUE;
        if (showUpload) {
            container.style.display = "";
            delete container.dataset.visible_controlled;
            return;
        }
        container.style.display = "none";
        container.dataset.visible_controlled = "true";
    };

    const refreshOptions = (): void => {
        const select = getSourceSelect();
        if (!select) {
            return;
        }

        const options = buildOptions();
        const desired = resolveSelectedValue(select.value, options);
        const newValuesJson = JSON.stringify(options.map((o) => o.value));
        const currentValuesJson = JSON.stringify(
            Array.from(select.options).map((o) => o.value),
        );
        if (newValuesJson === currentValuesJson && select.value === desired) {
            return;
        }

        select.innerHTML = "";
        for (const option of options) {
            const elem = document.createElement("option");
            elem.value = option.value;
            elem.text = option.label;
            elem.selected = option.value === desired;
            select.appendChild(elem);
        }
        select.value = desired;
        triggerChangeFor(select);
        applyUploadVisibility();
    };

    const onDocumentChange = (event: Event): void => {
        if ((event.target as HTMLElement | null)?.id === SOURCE_INPUT_ID) {
            applyUploadVisibility();
        }
    };

    const onDocumentDropdownInteraction = (event: Event): void => {
        if ((event.target as HTMLElement | null)?.id === SOURCE_INPUT_ID) {
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
            applyUploadVisibility();
        } catch (error) {
            console.log(
                "AudioSourceController: param build sync failed",
                error,
            );
        }
    };

    const scheduleInitialSync = (): void => {
        if (
            typeof postParamBuildSteps !== "undefined" &&
            Array.isArray(postParamBuildSteps)
        ) {
            postParamBuildSteps.push(runOnEachBuild);
            return;
        }
        setTimeout(scheduleInitialSync, 200);
    };

    document.addEventListener("change", onDocumentChange, true);
    document.addEventListener("mousedown", onDocumentDropdownInteraction);
    document.addEventListener("focusin", onDocumentDropdownInteraction);
    document.addEventListener(ACESTEPFUN_EVENT, refreshOptions);

    scheduleInitialSync();

    return {
        buildOptions,
        resolveSelectedValue,
        applyUploadVisibility,
        refreshOptions,
        runOnEachBuild,
        dispose: (): void => {
            document.removeEventListener("change", onDocumentChange, true);
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
