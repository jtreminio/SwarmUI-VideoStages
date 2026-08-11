import {
    buildCheckbox,
    resetRememberedAccordionSections,
} from "../detailWidgets";
import {
    getTimelineAuthoringSettings,
    setTimelineAuthoringSetting,
} from "../timelineAuthoringSettings";
import { createManagedModal, type ManagedModal } from "./modalManager";

const MODAL_CLASS = "vst-timeline-settings-modal";
const BACKDROP_CLASS = "vst-timeline-settings-backdrop";
const TITLE_ID = "vst_timeline_settings_title";
let currentModal: ManagedModal | null = null;

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
    const { header, body } = managed;
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
    close.addEventListener("click", managed.close);
    managed.open(close);
};
