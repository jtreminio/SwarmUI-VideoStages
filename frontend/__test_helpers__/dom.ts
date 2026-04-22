/**
 * Tiny DOM fixture builders for VideoStages frontend tests.
 *
 * Each helper attaches the created element(s) to `document.body` directly so
 * tests can stay declarative. The shared `setupFilesAfterEnv` scaffolding
 * wipes `document.body` after every test, so callers do not need to clean
 * these up by hand.
 *
 * Helpers are intentionally generic (no controller-specific defaults). When a
 * controller's tests need a higher-level fixture (e.g. "the trio of inputs
 * the audio source controller cares about"), compose these atoms in a small
 * local helper inside that test file.
 */

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

/**
 * Mounts a file input wrapped in the `.auto-input` container that SwarmUI's
 * param panel produces. `findParentOfClass(fileInput, "auto-input")` -- which
 * AudioSourceController and similar controllers rely on -- will resolve to
 * the wrapper.
 */
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
