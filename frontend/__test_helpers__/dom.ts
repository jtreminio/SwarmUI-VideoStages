interface MountSelectOptions {
    value?: string;
    options?: string[];
}

interface MountCheckboxOptions {
    checked?: boolean;
}

interface UploadRow {
    wrapper: HTMLElement;
    fileInput: HTMLInputElement;
}

export const mountSelect = (
    id: string,
    options: MountSelectOptions = {},
): HTMLSelectElement => {
    const select = document.createElement("select");
    select.id = id;
    for (const value of options.options ?? []) {
        const optionElement = document.createElement("option");
        optionElement.value = value;
        optionElement.text = value;
        select.appendChild(optionElement);
    }
    if (options.value !== undefined) {
        select.value = options.value;
    }
    document.body.appendChild(select);
    return select;
};

/** File input inside `.auto-input` so `findParentOfClass` matches SwarmUI's param panel. */
export const mountUploadRow = (id: string): UploadRow => {
    const wrapper = document.createElement("div");
    wrapper.className = "auto-input";
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.id = id;
    wrapper.appendChild(fileInput);
    document.body.appendChild(wrapper);
    return { wrapper, fileInput };
};

export const mountCheckbox = (
    id: string,
    options: MountCheckboxOptions = {},
): HTMLInputElement => {
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.id = id;
    checkbox.checked = !!options.checked;
    document.body.appendChild(checkbox);
    return checkbox;
};

const ensureTextarea = (id: string): HTMLTextAreaElement => {
    const existing = document.getElementById(id);
    if (existing instanceof HTMLTextAreaElement) {
        return existing;
    }
    const el = document.createElement("textarea");
    el.id = id;
    document.body.appendChild(el);
    return el;
};

export const mountVideoStagesData = (state: unknown): HTMLTextAreaElement => {
    const el = ensureTextarea("input_videostages");
    const persisted: unknown =
        typeof state === "object" &&
        state !== null &&
        !Array.isArray(state) &&
        !Object.hasOwn(state, "schemaVersion")
            ? { ...state, schemaVersion: 9 }
            : structuredClone(state);
    if (
        typeof persisted === "object" &&
        persisted !== null &&
        !Array.isArray(persisted) &&
        "schemaVersion" in persisted &&
        persisted.schemaVersion === 9 &&
        "clips" in persisted &&
        Array.isArray(persisted.clips)
    ) {
        for (const clip of persisted.clips) {
            if (typeof clip !== "object" || clip === null) continue;
            if ("frameRefs" in clip && !("keyframes" in clip)) {
                clip.keyframes = clip.frameRefs;
                delete clip.frameRefs;
            }
            if (!("stages" in clip) || !Array.isArray(clip.stages)) continue;
            for (const stage of clip.stages) {
                if (typeof stage !== "object" || stage === null) continue;
                if (
                    "frameRefStrengths" in stage &&
                    !("keyframeStrengths" in stage)
                ) {
                    stage.keyframeStrengths = stage.frameRefStrengths;
                    delete stage.frameRefStrengths;
                }
            }
        }
    }
    el.value =
        typeof persisted === "string" ? persisted : JSON.stringify(persisted);
    return el;
};

/** Core Video FPS param input — the timeline fps always mirrors this. */
export const mountVideoFps = (value: number): HTMLInputElement => {
    const existing = document.getElementById("input_videofps");
    if (existing instanceof HTMLInputElement) {
        existing.value = `${value}`;
        return existing;
    }
    const el = document.createElement("input");
    el.type = "number";
    el.id = "input_videofps";
    el.value = `${value}`;
    document.body.appendChild(el);
    return el;
};

export const mountPromptBox = (value = ""): HTMLTextAreaElement => {
    const el = ensureTextarea("input_prompt");
    el.value = value;
    return el;
};

/**
 * The clips passed to the FIRST `saveClips` call. Use when the test asserts that
 * exactly one save happened, or cares about the first of several.
 */
export const firstSavedClips = <T>(spy: {
    mock: { calls: [T, ...unknown[]][] };
}): T => spy.mock.calls[0][0];

/** The clips passed to the LAST `saveClips` call — the state the user ends on. */
export const lastSavedClips = <T>(spy: {
    mock: { calls: [T, ...unknown[]][] };
}): T => spy.mock.calls[spy.mock.calls.length - 1][0];

/** Pixels per second every timeline-track test mounts at. */
export const TIMELINE_PPS = 44;

/** The timeline body a track attaches to, at {@link TIMELINE_PPS}. */
export const mountTimelineBody = (pxPerSecond = TIMELINE_PPS): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    body.dataset.vstPps = String(pxPerSecond);
    document.body.appendChild(body);
    return body;
};

/**
 * jsdom does no layout, so a track that measures a lane or region sees all zeros.
 * Track code reads only `left` and `width`, but pass the row's `top` where it is
 * not 0: a rect whose vertical fields are all zero lets a reader that grabs
 * `rect.top` instead of `rect.left` keep on measuring correctly.
 */
export const stubRect = (
    el: HTMLElement,
    left: number,
    width: number,
    top = 0,
): void => {
    el.getBoundingClientRect = (() =>
        ({
            left,
            width,
            right: left + width,
            top,
            bottom: top,
            height: 0,
            x: left,
            y: top,
            toJSON: () => ({}),
        }) as DOMRect) as HTMLElement["getBoundingClientRect"];
};

/** The one element matching `selector`, or a throw naming what was missing. */
export const requireEl = (root: ParentNode, selector: string): HTMLElement => {
    const found = root.querySelector<HTMLElement>(selector);
    if (!found) {
        throw new Error(`not found: ${selector}`);
    }
    return found;
};

/** Tracks claim a press by event target, never by coordinate, so only clientX matters. */
export const mouse = (
    type: string,
    clientX: number,
    options: { shiftKey?: boolean } = {},
): MouseEvent =>
    new MouseEvent(type, {
        bubbles: true,
        clientX,
        button: 0,
        shiftKey: options.shiftKey ?? false,
    });
