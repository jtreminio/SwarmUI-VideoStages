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
    const persisted =
        typeof state === "object" &&
        state !== null &&
        !Array.isArray(state) &&
        !Object.hasOwn(state, "schemaVersion")
            ? { ...state, schemaVersion: 5 }
            : state;
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
