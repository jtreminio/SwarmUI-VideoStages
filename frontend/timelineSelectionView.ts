import { getSelection } from "./selection";

const SELECTED = "vst-selected";
const REGION_SELECTED = "vst-region-selected";

/**
 * Reflects the shared selection onto the track DOM, and is the ONLY owner of
 * both highlight classes. Clip-scoped selections get `.vst-region-selected` on
 * the clip region; every other selection kind gets `.vst-selected` on its own
 * mark/segment so the user can see which element the detail strip is bound to.
 *
 * Sole ownership is load-bearing: a stage chip stops click propagation, so a
 * second owner reacting to region clicks would leave the previously selected
 * region highlighted and the newly selected one bare.
 */
export const applySelectionHighlight = (body: HTMLElement): void => {
    const sel = getSelection();
    for (const el of body.querySelectorAll(`.${SELECTED}`)) {
        el.classList.remove(SELECTED);
    }
    for (const el of body.querySelectorAll(`.${REGION_SELECTED}`)) {
        el.classList.remove(REGION_SELECTED);
    }
    if (sel.kind === "clip" || sel.kind === "ic-lora") {
        body.querySelector(
            `.vst-region[data-clip-idx="${sel.clipIdx}"]`,
        )?.classList.add(REGION_SELECTED);
        return;
    }
    let selector: string | null = null;
    switch (sel.kind) {
        case "ref":
            selector = `.vst-refs-mark[data-clip-idx="${sel.clipIdx}"][data-ref-idx="${sel.refIdx}"]`;
            break;
        case "audio":
            selector = `.vst-audio-clip[data-clip-idx="${sel.clipIdx}"]`;
            break;
        case "audio-track":
            selector = `.vst-audio-seg[data-track-idx="${sel.trackIdx}"]`;
            break;
        case "prompt-major":
            selector = `.vst-major-seg[data-clip-idx="${sel.clipIdx}"]`;
            break;
        case "prompt-minor":
            selector = `.vst-minor-seg[data-clip-idx="${sel.clipIdx}"][data-window-idx="${sel.windowIdx}"]`;
            break;
        case "retake":
            selector = `.vst-retake[data-clip-idx="${sel.clipIdx}"]`;
            break;
        case "boundary":
            selector = `.vst-boundary-chip[data-left-clip-idx="${sel.leftClipIdx}"]`;
            break;
        default:
            selector = null;
    }
    if (selector) {
        body.querySelector(selector)?.classList.add(SELECTED);
    }
};
