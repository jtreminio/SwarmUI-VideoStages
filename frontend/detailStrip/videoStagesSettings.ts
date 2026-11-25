import {
    buildDetailActionButton,
    resetRememberedAccordionSections,
} from "../detailWidgets";
import {
    CHARACTER_REFINE_INSTALL_FEATURE,
    CHARACTER_REFINE_REPOSITORY_URL,
    CHARACTER_REFINE_REQUIRED_FEATURES,
} from "../generatedCharacterRefine";
import {
    PI_H3_INSTALL_FEATURE,
    PI_H3_REPOSITORY_URL,
    PI_H3_REQUIRED_FEATURES,
} from "../generatedPiH3";
import { getVideoStagesHostBridge } from "../host";
import { installHostFeature } from "../host/swarmUiAdapters";
import { availableLoraFolders, ROOT_LORA_FOLDER } from "../loraFolderFilter";
import { buildMultiSelectPicker } from "../multiSelectPicker";
import { getState, saveState } from "../persistence/repository";
import { getDropdownOptions } from "../swarmInputs";
import {
    getTimelineAuthoringSettings,
    setTimelineAuthoringSetting,
} from "../timelineAuthoringSettings";

const STORAGE_PREFIX = "videostages";

const buildNodeInstallSetting = (options: {
    key: string;
    title: string;
    name: string;
    description: string;
    repositoryUrl: string;
    installFeature: string;
    requiredFeatures: readonly string[];
}): HTMLElement => {
    const { key, title, name, description, repositoryUrl, installFeature } =
        options;
    const installed = options.requiredFeatures.every((feature) =>
        getVideoStagesHostBridge().hasBackendFeature(feature),
    );
    const section = document.createElement("fieldset");
    section.id = `vst_${key}_install`;
    section.className = `vst-node-install-setting vst-${key}-install-setting`;
    const legend = document.createElement("legend");
    legend.textContent = title;
    const hint = document.createElement("small");
    if (installed) {
        hint.textContent = `${name} is installed.`;
    } else {
        const repository = document.createElement("a");
        repository.href = repositoryUrl;
        repository.target = "_blank";
        repository.rel = "noopener noreferrer";
        repository.textContent = name;
        hint.append("Install ", repository, description);
    }
    const install = buildDetailActionButton({
        label: installed ? `${name} Installed` : `Install ${name}`,
        title: installed ? `${name} is installed` : `Install ${name}`,
        className: `vst-install-${key}`,
        disabled: installed,
        onClick: () => {
            if (!installHostFeature(installFeature, section.id)) {
                getVideoStagesHostBridge().showError(
                    "SwarmUI's ComfyUI feature installer is unavailable.",
                );
            }
        },
    });
    install.setAttribute(`data-vst-install-${key}`, "true");
    section.append(legend, hint, install);
    return section;
};

const buildLoraFolderSetting = (onChange: () => void): HTMLElement => {
    const folders = availableLoraFolders(
        getDropdownOptions("loras", "input_loras").values,
    );
    const saved = getTimelineAuthoringSettings().loraFolders;
    const section = document.createElement("fieldset");
    section.className = "vst-lora-folder-setting";
    const legend = document.createElement("legend");
    legend.textContent = "LoRA folders";
    const hint = document.createElement("small");
    hint.textContent =
        "Only checked top-level folders appear in VideoStages LoRA dropdowns.";
    const picker = buildMultiSelectPicker({
        label: "LoRA folders",
        placeholder: "No folders selected",
        filterPlaceholder: "Filter LoRA folders...",
        options: folders.map((folder) => ({
            value: folder,
            label: folder === ROOT_LORA_FOLDER ? "(Root)" : folder,
        })),
        selected: saved ?? folders,
        onChange: (included) => {
            setTimelineAuthoringSetting(
                "loraFolders",
                included.length === folders.length ? null : included,
            );
            onChange();
        },
    });
    section.append(legend, hint, picker);
    return section;
};

const resetVideoStages = (): void => {
    const empty = getState();
    empty.dimsExplicit = false;
    empty.clips = [];
    empty.audioTracks = [];
    saveState(empty, {
        notifyDomChange: true,
        origin: "timeline",
    });
    try {
        const keys: string[] = [];
        for (let index = 0; index < localStorage.length; index++) {
            const key = localStorage.key(index);
            if (key?.toLowerCase().startsWith(STORAGE_PREFIX)) {
                keys.push(key);
            }
        }
        for (const key of keys) {
            localStorage.removeItem(key);
        }
    } catch {}
    resetRememberedAccordionSections();
};

export const buildVideoStagesSettingsFields = (
    onLoraFoldersChanged: () => void,
): HTMLElement => {
    const fields = document.createElement("div");
    fields.className = "vst-videostages-settings-fields";
    fields.append(
        buildLoraFolderSetting(onLoraFoldersChanged),
        buildNodeInstallSetting({
            key: "bonita",
            title: "Make Them Bonita",
            name: "ComfyUI-Bonita",
            description: " - tracked detail repair for supported video models",
            repositoryUrl: CHARACTER_REFINE_REPOSITORY_URL,
            installFeature: CHARACTER_REFINE_INSTALL_FEATURE,
            requiredFeatures: CHARACTER_REFINE_REQUIRED_FEATURES,
        }),
        buildNodeInstallSetting({
            key: "pi-h3",
            title: "MiniMax pi-H3",
            name: "ComfyUI-piFlow",
            description: " for pi-H3 adapters on MiniMax H3 stages.",
            repositoryUrl: PI_H3_REPOSITORY_URL,
            installFeature: PI_H3_INSTALL_FEATURE,
            requiredFeatures: PI_H3_REQUIRED_FEATURES,
        }),
    );
    return fields;
};

export const buildResetVideoStagesButton = (
    afterReset: () => void,
): HTMLButtonElement =>
    buildDetailActionButton({
        label: "Reset VideoStages",
        title: "Clear all saved VideoStages data and settings",
        className: "small-button vst-reset-videostages",
        variant: "interrupt",
        onClick: () => {
            resetVideoStages();
            afterReset();
        },
    });
