import { setSelection } from "./uiState";

const CLIP_SELECTOR = '.vst-audio-clip[data-vst-audio="clip"]';

export interface TimelineAudioTrack {
    attach(body: HTMLElement): void;
    dispose(): void;
}

const parseClipIdx = (el: Element | null): number | null => {
    if (!el) {
        return null;
    }
    const raw = el.getAttribute("data-clip-idx");
    if (raw === null) {
        return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
};

/**
 * The audio track no longer owns an editor — clicking a clip's audio segment
 * simply selects it, and the bottom detail strip renders the editor.
 */
export const createTimelineAudioTrack = (): TimelineAudioTrack => {
    let boundBody: HTMLElement | null = null;

    const selectFromTarget = (target: Element): void => {
        const seg = target.closest(CLIP_SELECTOR);
        if (!(seg instanceof HTMLElement)) {
            return;
        }
        const clipIdx = parseClipIdx(seg);
        if (clipIdx === null) {
            return;
        }
        setSelection({ kind: "audio", clipIdx });
    };

    const onBodyClick = (event: Event): void => {
        if (event.target instanceof Element) {
            selectFromTarget(event.target);
        }
    };

    const onBodyKeyDown = (event: Event): void => {
        const ke = event as KeyboardEvent;
        if (ke.key !== "Enter" && ke.key !== " ") {
            return;
        }
        if (
            !(ke.target instanceof Element) ||
            !ke.target.closest(CLIP_SELECTOR)
        ) {
            return;
        }
        ke.preventDefault();
        selectFromTarget(ke.target);
    };

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("click", onBodyClick);
        body.addEventListener("keydown", onBodyKeyDown);
    };

    const dispose = (): void => {
        if (boundBody) {
            boundBody.removeEventListener("click", onBodyClick);
            boundBody.removeEventListener("keydown", onBodyKeyDown);
            boundBody = null;
        }
    };

    return { attach, dispose };
};
