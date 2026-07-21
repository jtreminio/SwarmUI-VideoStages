import { setSelection } from "./selection";
import { bindClickSelectableTrack, parseIntAttr } from "./trackDomUtils";

const CLIP_SELECTOR = '.vst-audio-clip[data-vst-audio="clip"]';

export interface TimelineAudioTrack {
    attach(body: HTMLElement): void;
    dispose(): void;
}

/**
 * The audio track no longer owns an editor — clicking a clip's audio segment
 * simply selects it, and the bottom detail strip renders the editor.
 */
export const createTimelineAudioTrack = (): TimelineAudioTrack => {
    let boundBody: HTMLElement | null = null;
    let unbind: (() => void) | null = null;

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        unbind = bindClickSelectableTrack(body, CLIP_SELECTOR, (el) => {
            const clipIdx = parseIntAttr(el, "data-clip-idx");
            if (clipIdx !== null) {
                setSelection({ kind: "audio", clipIdx });
            }
        });
    };

    const dispose = (): void => {
        unbind?.();
        unbind = null;
        boundBody = null;
    };

    return { attach, dispose };
};
