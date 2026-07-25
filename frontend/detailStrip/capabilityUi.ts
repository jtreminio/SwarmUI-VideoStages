import type { CapabilityDecision } from "../architectures/policy";

export const buildCapabilityNotice = (
    decision: CapabilityDecision,
): HTMLElement => {
    const notice = document.createElement("p");
    notice.className = "vst-detail-note vst-capability-notice";
    notice.textContent = decision.reason;
    notice.dataset.vstCapabilityUnsupported = "true";
    return notice;
};

/**
 * The delete/remove hooks a persisted-but-unsupported value must keep operable.
 * Listed once here instead of being restated at every call site.
 */
export const CAPABILITY_REPAIR_SELECTORS: readonly string[] = [
    ".vst-detail-delete",
    ".vst-stage-lora-remove",
];

export interface CapabilityRepairAction {
    label: string;
    /** Extra classes, for panel-specific test/style hooks. */
    className?: string;
    title?: string;
    onRepair: () => void;
}

/** A real focusable button, so every repair is reachable by keyboard alone. */
export const buildCapabilityRepairButton = (
    action: CapabilityRepairAction,
): HTMLButtonElement => {
    const button = document.createElement("button");
    button.type = "button";
    button.className =
        `interrupt-button vst-btn-tiny vst-capability-repair ${action.className ?? ""}`.trim();
    button.textContent = action.label;
    button.title = action.title ?? action.label;
    button.setAttribute("aria-label", button.title);
    button.addEventListener("click", action.onRepair);
    return button;
};

export const disableCapabilityControls = (
    root: HTMLElement | DocumentFragment,
    decision: CapabilityDecision,
    removableSelectors: readonly string[] = [],
): void => {
    const removable = new Set(
        removableSelectors.flatMap((selector) =>
            Array.from(root.querySelectorAll<HTMLElement>(selector)),
        ),
    );
    for (const control of root.querySelectorAll<
        | HTMLInputElement
        | HTMLSelectElement
        | HTMLTextAreaElement
        | HTMLButtonElement
    >("input, select, textarea, button")) {
        if (
            removable.has(control) ||
            [...removable].some((element) => element.contains(control))
        ) {
            continue;
        }
        control.disabled = true;
        control.title = decision.reason;
    }
    if (root instanceof DocumentFragment) {
        // Wrapperless sections: mark each top-level element instead.
        for (const child of root.children) {
            child.classList.add("vst-capability-readonly");
        }
    } else {
        root.classList.add("vst-capability-readonly");
    }
    root.prepend(buildCapabilityNotice(decision));
};

/**
 * The one persisted-but-unsupported contract: an unsupported value stays
 * VISIBLE and READ-ONLY with its reason, while creation and editing are
 * disabled and an explicit, keyboard-reachable remove/reset always survives —
 * so anything the document persists can be repaired from the dock alone.
 */
export const applyPersistedCapabilityRepair = (
    root: HTMLElement | DocumentFragment,
    decision: CapabilityDecision,
    options: {
        keep?: readonly string[];
        repair?: CapabilityRepairAction;
    } = {},
): void => {
    disableCapabilityControls(
        root,
        decision,
        options.keep ?? CAPABILITY_REPAIR_SELECTORS,
    );
    if (options.repair) {
        // Built after the disable pass, so the repair is never disabled by it.
        root.appendChild(buildCapabilityRepairButton(options.repair));
    }
};
