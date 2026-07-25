import {
    buildCheckbox,
    resetRememberedAccordionSections,
} from "../detailWidgets";
import {
    getTimelineAuthoringSettings,
    setTimelineAuthoringSetting,
} from "../timelineAuthoringSettings";

const MODAL_CLASS = "vst-timeline-settings-modal";
const BACKDROP_CLASS = "vst-timeline-settings-backdrop";
let currentCleanup: (() => void) | null = null;

export const closeTimelineAuthoringSettingsModal = (): void => {
    currentCleanup?.();
    currentCleanup = null;
    document.querySelector(`.${MODAL_CLASS}`)?.remove();
    document.querySelector(`.${BACKDROP_CLASS}`)?.remove();
};

export const openTimelineAuthoringSettingsModal = (): void => {
    closeTimelineAuthoringSettingsModal();
    const settings = getTimelineAuthoringSettings();

    const backdrop = document.createElement("div");
    backdrop.className = `modal-backdrop fade show ${BACKDROP_CLASS}`;

    const modal = document.createElement("div");
    modal.className = `modal fade show ${MODAL_CLASS}`;
    modal.style.display = "block";
    modal.tabIndex = -1;
    modal.setAttribute("role", "dialog");
    modal.setAttribute("aria-modal", "true");
    modal.setAttribute("aria-labelledby", "vst_timeline_settings_title");

    const dialog = document.createElement("div");
    dialog.className = "modal-dialog modal-dialog-centered";
    dialog.setAttribute("role", "document");
    const content = document.createElement("div");
    content.className = "modal-content";
    const header = document.createElement("div");
    header.className = "modal-header";
    const title = document.createElement("h5");
    title.className = "modal-title";
    title.id = "vst_timeline_settings_title";
    title.textContent = "Timeline Settings";
    const close = document.createElement("button");
    close.type = "button";
    close.className = "basic-button small-button";
    close.textContent = "×";
    close.title = "Close timeline settings";
    close.setAttribute("aria-label", close.title);
    header.append(title, close);

    const body = document.createElement("div");
    body.className = "modal-body";
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
    content.append(header, body);
    dialog.appendChild(content);
    modal.appendChild(dialog);

    const dismiss = (): void => {
        currentCleanup?.();
    };
    const onKeyDown = (event: KeyboardEvent): void => {
        if (event.key === "Escape") {
            dismiss();
        }
    };
    const cleanup = (): void => {
        document.removeEventListener("keydown", onKeyDown);
        modal.remove();
        backdrop.remove();
        if (currentCleanup === cleanup) {
            currentCleanup = null;
        }
    };
    currentCleanup = cleanup;
    close.addEventListener("click", dismiss);
    backdrop.addEventListener("click", dismiss);
    modal.addEventListener("mousedown", (event) => {
        if (event.target === modal) {
            dismiss();
        }
    });
    document.addEventListener("keydown", onKeyDown);
    document.body.append(backdrop, modal);
    close.focus();
};
