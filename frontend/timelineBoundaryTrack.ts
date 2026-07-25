import { setSelection } from "./selection";
import { bindClickSelectableTrack } from "./trackDomUtils";

const CHIP_SELECTOR = "[data-vst-boundary-chip]";

export interface TimelineBoundaryTrack {
    attach(body: HTMLElement): void;
    dispose(): void;
}

const parseLeftClipIdx = (el: Element | null): number | null => {
    if (!el) {
        return null;
    }
    const raw = el.getAttribute("data-left-clip-idx");
    if (raw === null) {
        return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
};

/**
 * Interior seam chips: clicking (or Enter/Space) selects clip N's outgoing boundary so the bottom
 * detail strip renders its editor. The strip's boundary section is the sole editor for the join mode.
 */
export const createTimelineBoundaryTrack = (): TimelineBoundaryTrack => {
    let boundBody: HTMLElement | null = null;
    let unbind: (() => void) | null = null;

    const activateFromTarget = (target: Element): void => {
        const chip = target.closest(CHIP_SELECTOR);
        if (!(chip instanceof HTMLElement)) {
            return;
        }
        const leftClipIdx = parseLeftClipIdx(chip);
        if (leftClipIdx === null) {
            return;
        }
        setSelection({ kind: "boundary", leftClipIdx });
    };

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        unbind = bindClickSelectableTrack(body, CHIP_SELECTOR, (el) =>
            activateFromTarget(el),
        );
    };

    const dispose = (): void => {
        unbind?.();
        unbind = null;
        boundBody = null;
    };

    return { attach, dispose };
};
