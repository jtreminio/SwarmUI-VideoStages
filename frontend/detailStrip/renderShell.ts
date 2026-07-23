import { getLtxHostBridge } from "../host";
import { isSameSelection } from "../selection";
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
    previousSelection: TimelineSelection | null;
    collapsed: boolean;
    clearSelection: () => void;
    toggleCollapsed: () => void;
}): void => {
    const previousBody =
        options.detail.querySelector<HTMLElement>(".vst-detail-body");
    const savedScroll = previousBody?.scrollTop ?? 0;
    options.focus.capture();
    options.detail.className = `${DETAIL_CLASS}${
        options.collapsed ? " vst-detail-collapsed" : ""
    }`;
    options.detail.innerHTML = "";
    options.detail.appendChild(
        buildDetailHeader(options.selection, options.clips, options.collapsed, {
            clearSelection: options.clearSelection,
            toggleCollapsed: options.toggleCollapsed,
        }),
    );
    if (!options.collapsed) {
        const body = buildDetailPanelBody(
            options.context,
            options.selection,
            options.clips,
        );
        options.detail.appendChild(body);
        if (
            options.selection.kind === "clip" ||
            options.selection.kind === "retake"
        ) {
            getLtxHostBridge().enableSliders(body);
        }
    }
    options.focus.restore(options.detail);
    const newBody =
        options.detail.querySelector<HTMLElement>(".vst-detail-body");
    if (newBody && savedScroll > 0) {
        newBody.scrollTop = savedScroll;
    }

    if (
        options.selection.kind === "retake" &&
        !options.collapsed &&
        !(
            options.previousSelection &&
            isSameSelection(options.selection, options.previousSelection)
        )
    ) {
        const active = document.activeElement;
        const retakeColumn = options.detail.querySelector<HTMLElement>(
            ".vst-detail-retake-col",
        );
        if (
            !(
                active instanceof HTMLElement && options.detail.contains(active)
            ) &&
            retakeColumn &&
            typeof retakeColumn.scrollIntoView === "function"
        ) {
            retakeColumn.scrollIntoView({ block: "nearest" });
        }
    }
    if (!options.collapsed) {
        options.focus.autoFocusSelection(options.detail, options.selection);
    }
};
