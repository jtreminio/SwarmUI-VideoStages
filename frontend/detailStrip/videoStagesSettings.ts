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
    H3_LATENT_UPSCALER_FEATURE,
    H3_LATENT_UPSCALER_INSTALL_FEATURE,
    H3_LATENT_UPSCALER_REPOSITORY_URL,
} from "../generatedMiniMaxLatentUpscaler";
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

const buildMiniMaxLatentUpscalerInstallSetting = (): HTMLElement => {
    const installed = getVideoStagesHostBridge().hasBackendFeature(
        H3_LATENT_UPSCALER_FEATURE,
    );
    const section = document.createElement("fieldset");
    section.id = "vst_minimax_latent_upscaler_install";
    const legend = document.createElement("legend");
    legend.textContent = "MiniMax H3 Latent Upscaler";
    const hint = document.createElement("small");
    if (installed) {
        hint.textContent = "MiniMax H3 Latent Upscaler is installed. ";
    } else {
        const repository = document.createElement("a");
        repository.href = H3_LATENT_UPSCALER_REPOSITORY_URL;
        repository.target = "_blank";
        repository.rel = "noopener noreferrer";
        repository.textContent = "LBH's MiniMax H3 Latent Upscaler";
        hint.append("Install ", repository, " for learned latent upscaling. ");
    }
    hint.append(
        "Place its 3D weights in latent_upscale_models and select them as the stage's Upscale Method.",
    );
    const install = buildDetailActionButton({
        label: installed
            ? "MiniMax H3 Latent Upscaler Installed"
            : "Install MiniMax H3 Latent Upscaler",
        title: installed
            ? "MiniMax H3 Latent Upscaler is installed"
            : "Install MiniMax H3 Latent Upscaler",
        className: "vst-install-minimax-latent-upscaler",
        disabled: installed,
        onClick: () => {
            if (
                !installHostFeature(
                    H3_LATENT_UPSCALER_INSTALL_FEATURE,
                    section.id,
                )
            ) {
                getVideoStagesHostBridge().showError(
                    "SwarmUI's ComfyUI feature installer is unavailable.",
                );
            }
        },
    });
    install.dataset.vstInstallMinimaxLatentUpscaler = "true";
    section.append(legend, hint, install);
    return section;
};

const buildBonitaInstallSetting = (): HTMLElement => {
    const installed = CHARACTER_REFINE_REQUIRED_FEATURES.every((feature) =>
        getVideoStagesHostBridge().hasBackendFeature(feature),
    );
    const section = document.createElement("fieldset");
    section.id = "vst_bonita_install";
    section.className = "vst-bonita-install-setting";
    const legend = document.createElement("legend");
    legend.textContent = "Make Them Bonita";
    const hint = document.createElement("small");
    if (installed) {
        hint.textContent = "ComfyUI-Bonita is installed.";
    } else {
        const repository = document.createElement("a");
        repository.href = CHARACTER_REFINE_REPOSITORY_URL;
        repository.target = "_blank";
        repository.rel = "noopener noreferrer";
        repository.textContent = "ComfyUI-Bonita";
        hint.append(
            "Install ",
            repository,
            " - tracked detail repair for supported video models",
        );
    }
    const install = buildDetailActionButton({
        label: installed
            ? "ComfyUI-Bonita Installed"
            : "Install ComfyUI-Bonita",
        title: installed
            ? "ComfyUI-Bonita is installed"
            : "Install ComfyUI-Bonita",
        className: "vst-install-bonita",
        disabled: installed,
        onClick: () => {
            if (
                !installHostFeature(
                    CHARACTER_REFINE_INSTALL_FEATURE,
                    section.id,
                )
            ) {
                getVideoStagesHostBridge().showError(
                    "SwarmUI's ComfyUI feature installer is unavailable.",
                );
            }
        },
    });
    install.dataset.vstInstallBonita = "true";
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
        buildBonitaInstallSetting(),
        buildMiniMaxLatentUpscalerInstallSetting(),
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
