import { getVideoStagesHostBridge } from "../host";
import type { Clip, TimelineSelection } from "../types";
import type { DetailStripContext } from "./context";
import type { DetailFocusSession } from "./focusSession";
import { buildDetailHeader, buildDetailPanelBody } from "./panelRouter";

const DETAIL_CLASS = "vst-detail";

export const renderDetailShell = (options: {
    detail: HTMLElement;
    context: DetailStripContext;
    focus: DetailFocusSession;
    clips: Clip[];
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
        buildDetailHeader(options.selection, options.clips),
    );
    const body = buildDetailPanelBody(
        options.context,
        options.selection,
        options.clips,
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
        const key =
            options.selection.kind === "ref"
                ? "references"
                : options.selection.kind === "audio-segment"
                  ? "audio-segments"
                  : options.selection.kind === "audio-track"
                    ? "audio-tracks"
                    : options.selection.kind === "audio-track-span"
                      ? "audio-track-spans"
                      : options.selection.kind === "prompt-minor"
                        ? "relay-prompts"
                        : options.selection.kind === "ic-lora"
                          ? "ic-loras"
                          : null;
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
