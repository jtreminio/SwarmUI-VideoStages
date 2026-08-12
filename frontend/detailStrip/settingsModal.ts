import {
    buildCheckbox,
    buildDetailActionButton,
    resetRememberedAccordionSections,
} from "../detailWidgets";
import { getState, saveState } from "../persistence/repository";
import {
    getTimelineAuthoringSettings,
    setTimelineAuthoringSetting,
} from "../timelineAuthoringSettings";
import { createManagedModal, type ManagedModal } from "./modalManager";

const MODAL_CLASS = "vst-timeline-settings-modal";
const BACKDROP_CLASS = "vst-timeline-settings-backdrop";
const TITLE_ID = "vst_timeline_settings_title";
const STORAGE_PREFIX = "videostages";
let currentModal: ManagedModal | null = null;

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

    let managed: ManagedModal;
    managed = createManagedModal({
        modalClass: MODAL_CLASS,
        backdropClass: BACKDROP_CLASS,
        labelledBy: TITLE_ID,
        onClose: () => {
            if (currentModal === managed) {
                currentModal = null;
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
