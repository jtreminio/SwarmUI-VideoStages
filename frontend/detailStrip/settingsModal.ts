import {
    buildCheckbox,
    buildDetailActionButton,
    resetRememberedAccordionSections,
} from "../detailWidgets";
import { availableLoraFolders, ROOT_LORA_FOLDER } from "../loraFolderFilter";
import { getState, saveState } from "../persistence/repository";
import { getDropdownOptions } from "../swarmInputs";
import {
    getTimelineAuthoringSettings,
    setTimelineAuthoringSetting,
    TIMELINE_AUTHORING_SETTINGS_CHANGED,
} from "../timelineAuthoringSettings";
import { createManagedModal, type ManagedModal } from "./modalManager";

const MODAL_CLASS = "vst-timeline-settings-modal";
const BACKDROP_CLASS = "vst-timeline-settings-backdrop";
const TITLE_ID = "vst_timeline_settings_title";
const STORAGE_PREFIX = "videostages";
let currentModal: ManagedModal | null = null;

const buildLoraFolderSetting = (onChange: () => void): HTMLElement => {
    const folders = availableLoraFolders(
        getDropdownOptions("loras", "input_loras").values,
    );
    const saved = getTimelineAuthoringSettings().loraFolders;
    const selected = new Set(saved ?? folders);
    const section = document.createElement("fieldset");
    section.className = "vst-lora-folder-setting";
    const legend = document.createElement("legend");
    legend.textContent = "LoRA folders";
    const hint = document.createElement("small");
    hint.textContent =
        "Only checked top-level folders appear in VideoStages LoRA dropdowns.";
    const options = document.createElement("div");
    options.className = "vst-lora-folder-options";
    const actions = document.createElement("div");
    actions.className = "vst-lora-folder-actions";

    const save = (): void => {
        const included = folders.filter((folder) => selected.has(folder));
        setTimelineAuthoringSetting(
            "loraFolders",
            included.length === folders.length ? null : included,
        );
        onChange();
    };
    for (const folder of folders) {
        const row = buildCheckbox(
            folder === ROOT_LORA_FOLDER ? "(Root)" : folder,
            selected.has(folder),
            (checked) => {
                if (checked) {
                    selected.add(folder);
                } else {
                    selected.delete(folder);
                }
                save();
            },
        );
        row.classList.add("vst-lora-folder-option");
        const input = row.querySelector<HTMLInputElement>(
            "input[type='checkbox']",
        );
        if (input) {
            input.value = folder;
        }
        options.appendChild(row);
    }
    const selectAll = (checked: boolean): void => {
        selected.clear();
        if (checked) {
            for (const folder of folders) {
                selected.add(folder);
            }
        }
        for (const input of options.querySelectorAll<HTMLInputElement>(
            "input[type='checkbox']",
        )) {
            input.checked = checked;
        }
        save();
    };
    actions.append(
        buildDetailActionButton({
            label: "All",
            title: "Include every LoRA folder",
            className: "small-button vst-lora-folders-all",
            onClick: () => selectAll(true),
        }),
        buildDetailActionButton({
            label: "None",
            title: "Exclude every LoRA folder",
            className: "small-button vst-lora-folders-none",
            onClick: () => selectAll(false),
        }),
    );
    section.append(legend, hint, actions, options);
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

export const closeTimelineAuthoringSettingsModal = (): void => {
    currentModal?.close();
};

export const openTimelineAuthoringSettingsModal = (): void => {
    closeTimelineAuthoringSettingsModal();
    const settings = getTimelineAuthoringSettings();
    let refreshOnClose = false;

    let managed: ManagedModal;
    managed = createManagedModal({
        modalClass: MODAL_CLASS,
        backdropClass: BACKDROP_CLASS,
        labelledBy: TITLE_ID,
        onClose: () => {
            if (currentModal === managed) {
                currentModal = null;
            }
            if (refreshOnClose) {
                window.dispatchEvent(
                    new Event(TIMELINE_AUTHORING_SETTINGS_CHANGED),
                );
            }
        },
    });
    currentModal = managed;
    const { content, header, body } = managed;
    const title = document.createElement("h5");
    title.className = "modal-title";
    title.id = TITLE_ID;
    title.textContent = "Timeline Settings";
    const close = document.createElement("button");
    close.type = "button";
    close.className = "basic-button small-button";
    close.textContent = "×";
    close.title = "Close timeline settings";
    close.setAttribute("aria-label", close.title);
    header.append(title, close);

    body.append(
        buildCheckbox("Snap", settings.snap, (value) =>
            setTimelineAuthoringSetting("snap", value),
        ),
        buildCheckbox("Auto-collapse", settings.autoCollapse, (value) => {
            setTimelineAuthoringSetting("autoCollapse", value);
            if (value) {
                resetRememberedAccordionSections();
            }
        }),
        buildLoraFolderSetting(() => {
            refreshOnClose = true;
        }),
    );
    const footer = document.createElement("div");
    footer.className = "modal-footer";
    footer.appendChild(
        buildDetailActionButton({
            label: "Reset VideoStages",
            title: "Clear all saved VideoStages data and settings",
            className: "small-button vst-reset-videostages",
            variant: "interrupt",
            onClick: () => {
                resetVideoStages();
                managed.close();
            },
        }),
    );
    content.appendChild(footer);
    close.addEventListener("click", managed.close);
    managed.open(close);
};
