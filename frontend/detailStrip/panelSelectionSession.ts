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
        collapsed: boolean,
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
        selection: TimelineSelection,
        dock: HTMLElement | null,
        collapsed: boolean,
        clips: Clip[],
    ): boolean => {
        if (!dock || !rendered || collapsed) {
            return false;
        }
        const previous = rendered;
        const active = document.activeElement;
        const fromOutside = !(
            active instanceof HTMLElement && dock.contains(active)
        );
        const swap = (
            rowSelector: string,
            activeClass: string,
            index: number,
        ): boolean => {
            const rows = Array.from(
                dock.querySelectorAll<HTMLElement>(rowSelector),
            );
            if (index < -1 || index >= rows.length) {
                return false;
            }
            rows.forEach((row, rowIndex) => {
                row.classList.toggle(activeClass, rowIndex === index);
            });
            rendered = selection;
            syncBreadcrumb(dock, clips);
            if (
                fromOutside &&
                index >= 0 &&
                typeof rows[index].scrollIntoView === "function"
            ) {
                rows[index].scrollIntoView({ block: "nearest" });
            }
            return true;
        };

        const previousIsAudio =
            previous.kind === "audio" || previous.kind === "audio-segment";
        const selectionIsAudio =
            selection.kind === "audio" || selection.kind === "audio-segment";
        if (previousIsAudio && selectionIsAudio) {
            // Audio segments now render one editor at a time, so swapping a
            // class cannot update the form's bound segment. Rebuild as stage
            // selection does instead.
            return false;
        }
        if (rendered.kind !== selection.kind) {
            return false;
        }
        if (
            selection.kind === "prompt-minor" &&
            previous.kind === "prompt-minor"
        ) {
            if (selection.clipIdx !== previous.clipIdx) {
                return false;
            }
            const swapped = swap(
                ".vst-detail-minor-window",
                "vst-detail-minor-active",
                selection.windowIdx,
            );
            if (swapped && fromOutside) {
                const editor = dock.querySelector<HTMLTextAreaElement>(
                    `.vst-detail-minor-window[data-vst-minor-window="${selection.windowIdx}"] textarea`,
                );
                if (editor) {
                    editor.focus();
                    const length = editor.value.length;
                    try {
                        editor.setSelectionRange(length, length);
                    } catch {}
                }
            }
            return swapped;
        }
        if (selection.kind === "ref" && previous.kind === "ref") {
            return (
                selection.clipIdx === previous.clipIdx &&
                swap(
                    ".vst-detail-ref-row",
                    "vst-detail-instance-active",
                    selection.refIdx,
                )
            );
        }
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
