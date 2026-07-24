import { isSameSelection } from "../selection";
import type { Clip, TimelineSelection } from "../types";
import { detailBreadcrumb } from "./panelRouter";

export interface PanelSelectionSession {
    getRendered(): TimelineSelection | null;
    setRendered(selection: TimelineSelection): void;
    clear(): void;
    syncBreadcrumb(dock: HTMLElement | null, clips: Clip[]): void;
    targetedReselect(
        selection: TimelineSelection,
        dock: HTMLElement | null,
        clips: Clip[],
    ): boolean;
}

export const createPanelSelectionSession = (): PanelSelectionSession => {
    let rendered: TimelineSelection | null = null;

    const syncBreadcrumb = (dock: HTMLElement | null, clips: Clip[]): void => {
        if (!dock || !rendered) {
            return;
        }
        const breadcrumb = dock.querySelector<HTMLElement>(".vst-detail-crumb");
        if (breadcrumb) {
            breadcrumb.textContent = detailBreadcrumb(rendered, clips);
        }
    };

    const targetedReselect = (
        _selection: TimelineSelection,
        _dock: HTMLElement | null,
        _clips: Clip[],
    ): boolean => {
        // Active-only repeaters bind their editor controls to one concrete
        // item. Selection changes must rebuild; swapping CSS classes would
        // leave stale callbacks pointing at the previously selected item.
        return false;
    };

    return {
        getRendered: () => rendered,
        setRendered: (selection) => {
            rendered = selection;
        },
        clear: () => {
            rendered = null;
        },
        syncBreadcrumb,
        targetedReselect,
    };
};

export const isRenderedSelection = (
    session: PanelSelectionSession,
    selection: TimelineSelection,
): boolean => {
    const rendered = session.getRendered();
    return !!rendered && isSameSelection(selection, rendered);
};
