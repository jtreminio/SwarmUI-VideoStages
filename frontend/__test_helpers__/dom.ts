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
