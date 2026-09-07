export interface MultiSelectOption {
    value: string;
    label: string;
}

export interface MultiSelectPickerOptions {
    label: string;
    placeholder: string;
    filterPlaceholder: string;
    options: MultiSelectOption[];
    selected: string[];
    onChange: (selected: string[]) => void;
}

let pickerSequence = 0;

const selectedValues = (picker: HTMLElement): string[] =>
    Array.from(
        picker.querySelectorAll<HTMLInputElement>(
            "[data-vst-multi-select-option]",
        ),
    )
        .filter((input) => input.checked)
        .map((input) => input.value);

const setOptionChecked = (
    picker: HTMLElement,
    value: string,
    checked: boolean,
): void => {
    for (const input of picker.querySelectorAll<HTMLInputElement>(
        "[data-vst-multi-select-option]",
    )) {
        if (input.value === value) {
            input.checked = checked;
        }
    }
};

export const buildMultiSelectPicker = (
    options: MultiSelectPickerOptions,
): HTMLElement => {
    const picker = document.createElement("div");
    picker.className = "vst-multi-select";
    picker.dataset.vstMultiSelect = "true";

    const nativeSelect = document.createElement("select");
    nativeSelect.className = "vst-multi-select-native";
    nativeSelect.multiple = true;
    nativeSelect.hidden = true;

    const trigger = document.createElement("div");
    trigger.className = "vst-multi-select-trigger";
    trigger.dataset.vstMultiSelectTrigger = "true";
    trigger.role = "combobox";
    trigger.tabIndex = 0;
    trigger.title = `${options.label} - click to edit`;
    trigger.setAttribute("aria-haspopup", "listbox");
    trigger.setAttribute("aria-expanded", "false");

    const pills = document.createElement("span");
    pills.className = "vst-multi-select-pills";
    const caret = document.createElement("span");
    caret.className = "vst-multi-select-caret";
    caret.textContent = "▾";
    trigger.append(pills, caret);

    const panel = document.createElement("div");
    panel.className = "vst-multi-select-panel";
    panel.dataset.vstMultiSelectPanel = "true";
    panel.hidden = true;
    panel.setAttribute("popover", "manual");
    panel.id = `vst_multi_select_panel_${++pickerSequence}`;
    trigger.setAttribute("aria-controls", panel.id);

    const filter = document.createElement("input");
    filter.type = "text";
    filter.className = "auto-text vst-multi-select-filter";
    filter.dataset.vstMultiSelectFilter = "true";
    filter.placeholder = options.filterPlaceholder;
    filter.autocomplete = "off";

    const list = document.createElement("div");
    list.className = "vst-multi-select-list";
    list.role = "listbox";
    list.setAttribute("aria-multiselectable", "true");
    const selected = new Set(options.selected);
    for (const option of options.options) {
        const nativeOption = document.createElement("option");
        nativeOption.value = option.value;
        nativeOption.textContent = option.label;
        nativeOption.selected = selected.has(option.value);
        nativeSelect.appendChild(nativeOption);

        const row = document.createElement("label");
        row.className = "vst-multi-select-option";
        const input = document.createElement("input");
        input.type = "checkbox";
        input.className = "auto-checkbox";
        input.dataset.vstMultiSelectOption = "true";
        input.value = option.value;
        input.checked = selected.has(option.value);
        const text = document.createElement("span");
        text.textContent = option.label;
        row.append(input, text);
        list.appendChild(row);
    }

    const actions = document.createElement("div");
    actions.className = "vst-multi-select-actions";
    const count = document.createElement("span");
    count.className = "vst-multi-select-count";
    const clear = document.createElement("button");
    clear.type = "button";
    clear.className = "basic-button small-button vst-multi-select-clear";
    clear.textContent = "Clear";
    actions.append(count, clear);
    panel.append(filter, list, actions);
    picker.append(nativeSelect, trigger, panel);

    const renderSelection = (): string[] => {
        const values = selectedValues(picker);
        const selectedSet = new Set(values);
        for (const option of Array.from(nativeSelect.options)) {
            option.selected = selectedSet.has(option.value);
        }
        pills.replaceChildren();
        for (const option of options.options) {
            if (!selectedSet.has(option.value)) {
                continue;
            }
            const pill = document.createElement("span");
            pill.className = "vst-multi-select-pill";
            pill.append(option.label);
            const remove = document.createElement("button");
            remove.type = "button";
            remove.className = "vst-multi-select-pill-remove";
            remove.dataset.vstMultiSelectRemove = option.value;
            remove.title = `Remove ${option.label}`;
            remove.setAttribute("aria-label", remove.title);
            remove.textContent = "×";
            pill.appendChild(remove);
            pills.appendChild(pill);
        }
        if (values.length === 0) {
            const placeholder = document.createElement("span");
            placeholder.className = "vst-multi-select-placeholder";
            placeholder.textContent = options.placeholder;
            pills.appendChild(placeholder);
        }
        count.textContent = `${values.length} selected`;
        return values;
    };

    const applyFilter = (): void => {
        const wanted = filter.value.trim().toLowerCase();
        for (const row of list.querySelectorAll<HTMLElement>(
            ".vst-multi-select-option",
        )) {
            row.style.display =
                !wanted || row.textContent?.toLowerCase().includes(wanted)
                    ? ""
                    : "none";
        }
    };

    const positionPanel = (): void => {
        const rect = trigger.getBoundingClientRect();
        const edge = 8;
        const gap = 4;
        const width = Math.min(rect.width, window.innerWidth - edge * 2);
        panel.style.width = `${width}px`;
        panel.style.left = `${Math.max(edge, Math.min(rect.left, window.innerWidth - width - edge))}px`;
        panel.style.maxHeight = "none";
        const height = panel.offsetHeight;
        const below = Math.max(
            0,
            window.innerHeight - rect.bottom - gap - edge,
        );
        const above = Math.max(0, rect.top - gap - edge);
        const upward = height > below && above > below;
        const available = upward ? above : below;
        panel.style.maxHeight = `${available}px`;
        panel.style.top = `${Math.max(edge, upward ? rect.top - gap - Math.min(height, available) : rect.bottom + gap)}px`;
    };

    let cleanup: (() => void) | null = null;
    const setOpen = (open: boolean): void => {
        panel.hidden = !open;
        trigger.setAttribute("aria-expanded", `${open}`);
        cleanup?.();
        cleanup = null;
        if (!open) {
            panel.hidePopover?.();
            return;
        }
        filter.value = "";
        applyFilter();
        // The top layer escapes clipping without moving controls out of their owner.
        panel.showPopover?.();
        positionPanel();
        const closeOutside = (event: PointerEvent): void => {
            if (!picker.contains(event.target as Node)) {
                setOpen(false);
            }
        };
        const closeOnScroll = (event: Event): void => {
            if (!panel.contains(event.target as Node)) setOpen(false);
        };
        const observer = new MutationObserver(() => {
            if (!picker.isConnected) setOpen(false);
        });
        observer.observe(document.body, { childList: true, subtree: true });
        document.addEventListener("pointerdown", closeOutside);
        document.addEventListener("scroll", closeOnScroll, true);
        window.addEventListener("resize", positionPanel);
        cleanup = () => {
            observer.disconnect();
            document.removeEventListener("pointerdown", closeOutside);
            document.removeEventListener("scroll", closeOnScroll, true);
            window.removeEventListener("resize", positionPanel);
        };
        filter.focus({ preventScroll: true });
    };

    const commitSelection = (): void => {
        options.onChange(renderSelection());
    };

    picker.addEventListener("click", (event) => {
        const target = event.target as HTMLElement;
        const remove = target.closest<HTMLElement>(
            "[data-vst-multi-select-remove]",
        );
        if (remove) {
            setOptionChecked(
                picker,
                remove.dataset.vstMultiSelectRemove ?? "",
                false,
            );
            commitSelection();
            event.stopPropagation();
            return;
        }
        if (target.closest("[data-vst-multi-select-trigger]")) {
            setOpen(panel.hidden !== false);
            return;
        }
        if (target.closest(".vst-multi-select-clear")) {
            for (const input of picker.querySelectorAll<HTMLInputElement>(
                "[data-vst-multi-select-option]",
            )) {
                input.checked = false;
            }
            commitSelection();
        }
    });
    picker.addEventListener("change", (event) => {
        if (
            (event.target as HTMLElement).matches(
                "[data-vst-multi-select-option]",
            )
        ) {
            commitSelection();
        }
    });
    filter.addEventListener("input", () => {
        applyFilter();
        if (!panel.hidden) positionPanel();
    });
    picker.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
            setOpen(false);
            trigger.focus();
            return;
        }
        if (
            (event.key === "Enter" || event.key === " ") &&
            event.target === trigger
        ) {
            event.preventDefault();
            setOpen(panel.hidden !== false);
        }
    });

    renderSelection();
    return picker;
};
