import { getVideoStagesHostBridge } from "../host";
import type { AuthoringDocument, TimelineSelection } from "../types";
import type { DetailStripContext } from "./context";
import type { DetailFocusSession } from "./focusSession";
import { buildDetailHeader, buildDetailPanelBody } from "./panelRouter";

const DETAIL_CLASS = "vst-detail";

/**
 * The `data-vst-repeater-key` of the repeating section that owns a selection,
 * so the render can scroll it into view. Must match the key its panel builds
 * with.
 */
export const revealRepeaterKey = (
    selection: TimelineSelection,
): string | null => {
    switch (selection.kind) {
        case "ref":
            return "references";
        case "boundary-ref":
            return "clip-references";
        case "audio-track":
            return "audio-tracks";
        case "prompt-minor":
            return "relay-prompts";
        case "ic-lora":
            return "ic-loras";
        default:
            return null;
    }
};

export const renderDetailShell = (options: {
    detail: HTMLElement;
    context: DetailStripContext;
    focus: DetailFocusSession;
    state: AuthoringDocument;
    selection: TimelineSelection;
    revealSelection: boolean;
}): void => {
    const previousBody =
        options.detail.querySelector<HTMLElement>(".vst-detail-body");
    const savedScroll = previousBody?.scrollTop ?? 0;
    options.focus.capture();
    options.detail.className = DETAIL_CLASS;
    options.detail.innerHTML = "";
    options.detail.appendChild(
        buildDetailHeader(
            options.selection,
            options.state,
            options.context.authoring().capabilities,
        ),
    );
    const body = buildDetailPanelBody(
        options.context,
        options.selection,
        options.state,
    );
    options.detail.appendChild(body);
    // SwarmUI renders the paired number/range controls but wires their
    // bidirectional synchronization separately after they enter the DOM.
    // Initialize every detail body so sliders added to any panel work
    // without requiring a selection-kind allowlist here.
    getVideoStagesHostBridge().enableSliders(body);
    options.focus.restore(options.detail);
    const newBody =
        options.detail.querySelector<HTMLElement>(".vst-detail-body");
    if (newBody && savedScroll > 0) {
        newBody.scrollTop = savedScroll;
    }

    if (options.revealSelection) {
        const key = revealRepeaterKey(options.selection);
        const target =
            options.selection.kind === "retake"
                ? options.detail.querySelector<HTMLElement>(
                      '[data-vst-accordion-key="retake"]',
                  )
                : key
                  ? options.detail.querySelector<HTMLElement>(
                        `[data-vst-repeater-key="${key}"]`,
                    )
                  : null;
        if (target && typeof target.scrollIntoView === "function") {
            target.scrollIntoView({ block: "nearest" });
        }
    }
    options.focus.autoFocusSelection(options.detail, options.selection);
};
