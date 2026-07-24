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
