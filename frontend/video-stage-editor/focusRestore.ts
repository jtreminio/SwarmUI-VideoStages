export type FocusSnapshot = {
    selector: string;
    start: number | null;
    end: number | null;
} | null;

export const captureFocus = (): FocusSnapshot => {
    const el = document.activeElement;
    if (
        !el ||
        el === document.body ||
        (el.tagName !== "INPUT" && el.tagName !== "SELECT")
    ) {
        return null;
    }
    const dataset = (el as HTMLElement).dataset;
    let selector: string | null = null;
    if (dataset.clipField && dataset.clipIdx) {
        selector = `[data-clip-field="${dataset.clipField}"][data-clip-idx="${dataset.clipIdx}"]`;
    } else if (dataset.stageField && dataset.stageIdx && dataset.clipIdx) {
        selector = `[data-stage-field="${dataset.stageField}"][data-stage-idx="${dataset.stageIdx}"][data-clip-idx="${dataset.clipIdx}"]`;
    } else if (dataset.refField && dataset.refIdx && dataset.clipIdx) {
        selector = `[data-ref-field="${dataset.refField}"][data-ref-idx="${dataset.refIdx}"][data-clip-idx="${dataset.clipIdx}"]`;
    }
    if (!selector) {
        return null;
    }
    let start: number | null = null;
    let end: number | null = null;
    try {
        const inputEl = el as HTMLInputElement;
        start = inputEl.selectionStart;
        end = inputEl.selectionEnd;
    } catch {}
    return { selector, start, end };
};

export const restoreFocus = (snapshot: FocusSnapshot): void => {
    if (!snapshot) {
        return;
    }
    const el = document.querySelector(snapshot.selector) as
        | HTMLInputElement
        | HTMLSelectElement
        | null;
    if (!el) {
        return;
    }
    el.focus();
    if (
        el instanceof HTMLInputElement &&
        snapshot.start != null &&
        snapshot.end != null
    ) {
        try {
            el.setSelectionRange(snapshot.start, snapshot.end);
        } catch {}
    }
};
