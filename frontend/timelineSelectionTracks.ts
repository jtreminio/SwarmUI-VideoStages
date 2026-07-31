import { setSelection } from "./selection";
import { isActivateKey, parseIntAttr } from "./trackDomUtils";
import type { TimelineSelection } from "./types";

interface SelectionRule {
    selector: string;
    select(element: HTMLElement): TimelineSelection | null;
}

const RULES: readonly SelectionRule[] = [
    {
        selector: '.vst-audio-clip[data-vst-audio="clip"]',
        select: (element) => {
            const clipIdx = parseIntAttr(element, "data-clip-idx");
            return clipIdx === null ? null : { kind: "audio", clipIdx };
        },
    },
    {
        selector: "[data-vst-boundary-chip]",
        select: (element) => {
            const leftClipIdx = parseIntAttr(element, "data-left-clip-idx");
            return leftClipIdx === null
                ? null
                : { kind: "boundary", leftClipIdx };
        },
    },
];

export interface TimelineSelectionTracks {
    attach(body: HTMLElement): void;
    dispose(): void;
}

/**
 * One delegated controller for timeline elements whose entire behavior is
 * selecting a detail-strip target. Gesture-heavy tracks retain their own
 * controllers.
 */
export const createTimelineSelectionTracks = (): TimelineSelectionTracks => {
    let boundBody: HTMLElement | null = null;
    const selector = RULES.map((rule) => rule.selector).join(", ");

    const activate = (target: Element): void => {
        const element = target.closest(selector);
        if (!(element instanceof HTMLElement)) {
            return;
        }
        const selection = RULES.find((rule) =>
            element.matches(rule.selector),
        )?.select(element);
        if (selection) {
            setSelection(selection);
        }
    };
    const onClick = (event: Event): void => {
        if (event.target instanceof Element) {
            activate(event.target);
        }
    };
    const onKeyDown = (event: KeyboardEvent): void => {
        if (
            !isActivateKey(event) ||
            !(event.target instanceof Element) ||
            !event.target.closest(selector)
        ) {
            return;
        }
        event.preventDefault();
        activate(event.target);
    };

    const dispose = (): void => {
        boundBody?.removeEventListener("click", onClick);
        boundBody?.removeEventListener("keydown", onKeyDown);
        boundBody = null;
    };

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("click", onClick);
        body.addEventListener("keydown", onKeyDown);
    };

    return { attach, dispose };
};
