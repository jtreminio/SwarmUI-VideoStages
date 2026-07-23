import { clamp } from "./constants";
import { getVideoStagesHostBridge } from "./host";
import {
    enhanceHostPromptEditor,
    hasHostInputBrowser,
    openHostInputBrowser,
    renderHostSlider,
    showHostPopover,
} from "./host/swarmUiAdapters";

let sliderSeq = 0;
let helpSeq = 0;

const slugify = (value: string): string =>
    value
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, "-")
        .replace(/(^-|-$)/g, "") || "field";

/**
 * Append SwarmUI's native "?" info-popover to a field: a qbutton span inside
 * the label plus a `.sui-popover` div the host's `doPopover` toggles (the host
 * styles/positions it). `doPopover` lives in ui_improvements.js, which is not
 * loaded in jest, so the click is guarded. Text-only content — no HTML
 * injection into the popover body.
 */
export const appendHelp = (
    labelEl: HTMLElement,
    row: HTMLElement,
    fieldName: string,
    helpText: string,
): void => {
    const key = `vst_${slugify(fieldName)}_${++helpSeq}`;
    const btn = document.createElement("span");
    btn.className = "auto-input-qbutton info-popover-button";
    btn.textContent = "?";
    btn.addEventListener("click", (event) => {
        showHostPopover(key, event);
    });
    labelEl.appendChild(btn);
    const pop = document.createElement("div");
    pop.className = "sui-popover sui-info-popover";
    pop.id = `popover_${key}`;
    const name = document.createElement("b");
    name.textContent = fieldName;
    pop.append(
        name,
        document.createElement("br"),
        document.createTextNode(helpText),
    );
    row.appendChild(pop);
};

const wireNumericInput = (
    input: HTMLInputElement,
    fallback: number,
    min: number,
    max: number,
    onChange: (value: number) => void,
): void => {
    const apply = (normalize: boolean): void => {
        const parsed = Number.parseFloat(input.value);
        const next = clamp(
            Number.isFinite(parsed) ? parsed : fallback,
            min,
            max,
        );
        onChange(next);
        if (normalize) {
            input.value = `${next}`;
        }
    };
    input.addEventListener("input", () => apply(false));
    input.addEventListener("change", () => apply(true));
};

const boxClassFor = (control: HTMLElement): string | null => {
    if (control.classList.contains("auto-dropdown")) {
        return "auto-dropdown-box";
    }
    if (control.classList.contains("auto-number")) {
        return "auto-number-box";
    }
    if (control.classList.contains("auto-text")) {
        return "auto-text-box";
    }
    return null;
};

export const buildField = (
    label: string,
    control: HTMLElement,
    hint?: string,
    help?: string,
): HTMLElement => {
    const row = document.createElement("div");
    row.className = "auto-input vst-detail-field";
    const boxClass = boxClassFor(control);
    if (boxClass) {
        row.classList.add(boxClass);
    }
    const labelEl = document.createElement("label");
    const text = document.createElement("span");
    text.className = "auto-input-name vst-detail-field-label";
    text.textContent = label;
    labelEl.appendChild(text);
    if (help) {
        appendHelp(labelEl, row, label, help);
    }
    row.append(labelEl, control);
    if (hint) {
        const small = document.createElement("small");
        small.className = "vst-detail-field-hint";
        small.textContent = hint;
        row.appendChild(small);
    }
    return row;
};

export interface OptionSpec {
    value: string;
    label: string;
    disabled?: boolean;
}

export const buildOptionSelect = (
    specs: OptionSpec[],
    selected: string,
    onChange: (value: string) => void,
): HTMLSelectElement => {
    const select = document.createElement("select");
    select.className = "auto-dropdown vst-audio-select";
    for (const spec of specs) {
        const opt = document.createElement("option");
        opt.value = spec.value;
        opt.textContent = spec.label;
        opt.dataset.cleanname = spec.label;
        opt.disabled = spec.disabled === true;
        opt.selected = spec.value === selected;
        select.appendChild(opt);
    }
    select.addEventListener("change", () => onChange(select.value));
    return select;
};

export const buildSelect = (
    values: string[],
    labels: string[],
    selected: string,
    onChange: (value: string) => void,
): HTMLSelectElement =>
    buildOptionSelect(
        values.map((value, i) => ({ value, label: labels[i] ?? value })),
        selected,
        onChange,
    );

export const buildNumber = (
    value: number,
    min: number,
    max: number,
    step: number,
    onChange: (value: number) => void,
): HTMLInputElement => {
    const input = document.createElement("input");
    input.type = "number";
    input.className = "auto-number vst-refs-num";
    input.min = `${min}`;
    input.max = `${max}`;
    input.step = `${step}`;
    input.value = `${value}`;
    wireNumericInput(input, value, min, max, onChange);
    return input;
};

export const buildSlider = (
    label: string,
    value: number,
    min: number,
    max: number,
    step: number,
    onChange: (value: number) => void,
    opts?: {
        hint?: string;
        title?: string;
        help?: string;
        sliderMin?: number;
        sliderMax?: number;
        numberStep?: number | "any";
    },
): HTMLElement => {
    const holder = document.createElement("div");
    holder.className = "vst-stage-slider";
    const id = `vst_stage_slider_${++sliderSeq}`;
    holder.innerHTML = renderHostSlider({
        id,
        label,
        value,
        min,
        max,
        viewMin: opts?.sliderMin,
        viewMax: opts?.sliderMax,
        step,
    });
    const number = holder.querySelector<HTMLInputElement>(
        "input.auto-slider-number",
    );
    if (number) {
        number.step = `${opts?.numberStep ?? step}`;
        wireNumericInput(number, value, min, max, onChange);
    }
    if (opts?.title) {
        holder.title = opts.title;
    }
    if (opts?.help) {
        const labelEl = holder.querySelector<HTMLElement>("label");
        if (labelEl) {
            appendHelp(labelEl, holder, label, opts.help);
        }
    }
    if (opts?.hint) {
        const small = document.createElement("small");
        small.className = "vst-detail-field-hint";
        small.textContent = opts.hint;
        holder.appendChild(small);
    }
    return holder;
};

export const buildCheckbox = (
    label: string,
    checked: boolean,
    onChange: (value: boolean) => void,
    opts?: { disabled?: boolean; help?: string },
): HTMLElement => {
    const row = document.createElement("label");
    row.className =
        "auto-input auto-checkbox-box auto-input-flex vst-detail-field vst-detail-field-check";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.className = "auto-checkbox";
    input.checked = checked;
    input.addEventListener("change", () => onChange(input.checked));
    const text = document.createElement("span");
    text.className = "auto-input-name vst-detail-field-label";
    text.textContent = label;
    row.append(input, text);
    if (opts?.help) {
        appendHelp(row, row, label, opts.help);
    }
    if (opts?.disabled) {
        row.classList.add("vst-audio-disabled");
        input.setAttribute("disabled", "");
    }
    return row;
};

export const buildTextarea = (
    value: string,
    placeholder: string,
    focusKey: string,
    onInput: (value: string) => void,
): HTMLTextAreaElement => {
    const editor = document.createElement("textarea");
    editor.className =
        "auto-text auto-text-block vst-prompt-editor vst-detail-prompt";
    editor.value = value;
    editor.placeholder = placeholder;
    editor.setAttribute("data-vst-focus-key", focusKey);
    editor.addEventListener("input", () => onInput(editor.value));
    enhanceHostPromptEditor(editor);
    return editor;
};

const readFileAsDataUri = (
    file: File,
    onFile: (data: string, fileName: string) => void,
): void => {
    const reader = new FileReader();
    reader.onload = () => {
        const data = `${reader.result ?? ""}`;
        if (data) {
            onFile(data, file.name);
        }
    };
    reader.readAsDataURL(file);
};

let mediaPickCounter = 0;

/**
 * Media file picker offering both a browser upload and (when the host page
 * provides the input browser) SwarmUI's "Select" server-file browser — the
 * same pairing core file params get. `accept` filters the browser upload and
 * `browserTypes` (e.g. ["image"], ["audio"], ["image", "video"]) filters the
 * host input browser. Both paths resolve to a data URI before `onFile`; a
 * server pick lands as a View/ URL in `dataset.filedata` (written by site.js
 * `setMediaFileDirect`, which also needs the hidden preview and filename nodes
 * below) and is converted with the host's `toDataURL`.
 */
export const buildMediaPickRow = (
    label: string,
    accept: string,
    browserTypes: string[],
    name: string | null | undefined,
    onFile: (data: string, fileName: string) => void,
    onClear: () => void,
): HTMLElement => {
    const row = document.createElement("div");
    row.className = "auto-input vst-detail-field vst-audio-upload";
    const pickLabel = document.createElement("span");
    pickLabel.className = "auto-input-name vst-detail-field-label";
    pickLabel.textContent = label;
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = accept;
    fileInput.id = `vst-media-pick-${++mediaPickCounter}`;
    const fileName = document.createElement("span");
    fileName.className = "vst-audio-upload-name";
    fileName.textContent = name ? name : "No file chosen";
    const preview = document.createElement("div");
    preview.className = "auto-input-preview";
    preview.hidden = true;
    const previewName = document.createElement("span");
    previewName.className = "auto-file-input-filename";
    previewName.hidden = true;
    const clearBtn = document.createElement("button");
    clearBtn.type = "button";
    clearBtn.className = "basic-button small-button vst-audio-upload-clear";
    clearBtn.textContent = "Clear";
    clearBtn.hidden = !name;
    fileInput.addEventListener("change", () => {
        const file = fileInput.files?.[0];
        if (file) {
            readFileAsDataUri(file, onFile);
            return;
        }
        const picked = fileInput.dataset.filedata ?? "";
        if (!picked) {
            return;
        }
        const pickedName = fileInput.dataset.filename ?? "server file";
        if (picked.startsWith("data:")) {
            onFile(picked, pickedName);
            return;
        }
        void getVideoStagesHostBridge()
            .toDataUrl(picked)
            .then((data) => onFile(data, pickedName));
    });
    clearBtn.addEventListener("click", () => onClear());
    row.append(pickLabel, fileInput, fileName, clearBtn, preview, previewName);
    if (hasHostInputBrowser()) {
        const selectBtn = document.createElement("button");
        selectBtn.type = "button";
        selectBtn.className = "basic-button small-button vst-media-pick-select";
        selectBtn.textContent = "Select";
        selectBtn.addEventListener("click", () =>
            openHostInputBrowser(fileInput.id, browserTypes),
        );
        clearBtn.before(selectBtn);
    }
    return row;
};

export const sectionLabel = (text: string): HTMLElement => {
    const sec = document.createElement("div");
    sec.className = "vst-detail-sec vst-detail-wrap-sec";
    sec.textContent = text;
    return sec;
};

export interface RepeatingGroupItem {
    label: string;
    focusKey?: string;
    title?: string;
    active?: boolean;
    className?: string;
    onSelect: () => void;
    onShiftDelete?: () => void;
    onDelete?: () => void;
    deleteTitle?: string;
    deleteDisabled?: boolean;
    headerAction?: {
        label: string;
        title: string;
        className?: string;
        active?: boolean;
        onClick: () => void;
    };
}

export interface RepeatingGroupAddAction {
    title: string;
    className: string;
    disabled?: boolean;
    onClick: () => void;
}

export interface RepeatingEditorSpec {
    key: string;
    label: string;
    items: readonly RepeatingGroupItem[];
    add: RepeatingGroupAddAction;
    remove: {
        title: string;
        className: string;
    };
    editor?: HTMLElement;
    sectionClass?: string;
    listClass?: string;
}

/**
 * Canonical repeating-child section. Every repeater gets the same title,
 * collapsible input-group items, per-item actions, an Add button, active-editor slot, and stable key
 * used by the detail shell for scroll preservation and external reveal.
 */
export const buildRepeatingEditor = (
    spec: RepeatingEditorSpec,
): {
    section: HTMLElement;
    heading: HTMLElement;
    list: HTMLElement;
    editor: HTMLElement | null;
} => {
    const section = document.createElement("div");
    section.className =
        `vst-detail-stages-wrap vst-detail-repeating-editor ${spec.sectionClass ?? ""}`.trim();
    section.dataset.vstRepeaterKey = spec.key;
    const heading = sectionLabel(spec.label);
    section.appendChild(heading);
    const list = document.createElement("div");
    list.className =
        `vst-detail-repeating-group-list ${spec.listClass ?? ""}`.trim();
    spec.items.forEach((item, index) => {
        const active = item.active === true;
        const group = document.createElement("div");
        group.className = `input-group vst-detail-repeating-group ${
            active ? "input-group-open" : "input-group-closed"
        }`;
        const header = document.createElement("div");
        header.className =
            `input-group-header input-group-shrinkable vst-detail-repeating-group-header ${item.className ?? ""}`.trim();
        header.tabIndex = 0;
        header.setAttribute("role", "button");
        header.setAttribute("aria-expanded", `${active}`);
        header.setAttribute("aria-pressed", `${active}`);
        if (item.focusKey) {
            header.dataset.vstFocusKey = item.focusKey;
        }
        const labelWrap = document.createElement("span");
        labelWrap.className = "header-label-wrap";
        const symbol = document.createElement("span");
        symbol.className = "auto-symbol";
        symbol.textContent = active ? "▾" : "▸";
        const label = document.createElement("span");
        label.className = "header-label";
        label.textContent = item.label;
        const spacer = document.createElement("span");
        spacer.className = "header-label-spacer";
        const actions = document.createElement("span");
        actions.className = "vst-detail-repeating-group-actions";
        if (item.headerAction) {
            const action = document.createElement("button");
            action.type = "button";
            action.className =
                `basic-button small-button vst-detail-repeating-group-action ${item.headerAction.className ?? ""}`.trim();
            action.textContent = item.headerAction.label;
            action.title = item.headerAction.title;
            action.setAttribute("aria-label", item.headerAction.title);
            action.classList.toggle(
                "vst-detail-repeating-group-action-active",
                item.headerAction.active === true,
            );
            action.addEventListener("click", (event) => {
                event.preventDefault();
                event.stopPropagation();
                item.headerAction?.onClick();
            });
            actions.appendChild(action);
        }
        const onDelete = item.onDelete ?? item.onShiftDelete;
        if (onDelete) {
            const remove = document.createElement("button");
            remove.type = "button";
            remove.className =
                `basic-button small-button vst-refs-delete vst-detail-delete vst-detail-repeating-group-delete ${spec.remove.className}`.trim();
            remove.textContent = "- Delete";
            remove.title =
                item.deleteTitle ??
                (active ? spec.remove.title : `Delete ${item.label}`);
            remove.setAttribute("aria-label", remove.title);
            remove.disabled = item.deleteDisabled === true;
            remove.addEventListener("click", (event) => {
                event.preventDefault();
                event.stopPropagation();
                onDelete();
            });
            actions.appendChild(remove);
        }
        labelWrap.append(symbol, label, spacer, actions);
        header.appendChild(labelWrap);
        const content = document.createElement("div");
        content.className =
            "input-group-content vst-detail-repeating-group-content";
        if (active && spec.editor) {
            spec.editor.classList.add("vst-detail-repeating-editor-active");
            content.appendChild(spec.editor);
        } else {
            content.hidden = true;
        }
        const activateOrToggle = (): void => {
            if (!active) {
                item.onSelect();
                return;
            }
            const opening = content.hidden === true;
            content.hidden = !opening;
            group.classList.toggle("input-group-open", opening);
            group.classList.toggle("input-group-closed", !opening);
            header.setAttribute("aria-expanded", `${opening}`);
            symbol.textContent = opening ? "▾" : "▸";
        };
        header.addEventListener("click", activateOrToggle);
        header.addEventListener("keydown", (event) => {
            if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                activateOrToggle();
            }
        });
        group.append(header, content);
        list.appendChild(group);
        group.dataset.vstRepeaterItem = `${index}`;
    });
    section.appendChild(list);
    const add = document.createElement("button");
    add.type = "button";
    add.className =
        `basic-button small-button vst-add-btn vst-detail-repeating-add ${spec.add.className}`.trim();
    add.textContent = "+ Add";
    add.title = spec.add.title;
    add.setAttribute("aria-label", spec.add.title);
    add.disabled = spec.add.disabled === true;
    add.addEventListener("click", (event) => {
        event.preventDefault();
        spec.add.onClick();
    });
    section.appendChild(add);
    return { section, heading, list, editor: spec.editor ?? null };
};

export const clampStartLength = (
    start: number,
    length: number,
    clipDur: number,
    minLength: number,
): { start: number; length: number } => {
    const s = clamp(start, 0, Math.max(0, clipDur - minLength));
    const l = clamp(length, minLength, Math.max(minLength, clipDur - s));
    return { start: s, length: l };
};

export const buildGroup = (
    groupId: string,
    content: HTMLElement,
): HTMLElement => {
    const group = document.createElement("div");
    group.className = "input-group input-group-open";
    group.id = `auto-group-${groupId}`;

    const contentEl = document.createElement("div");
    contentEl.className = "input-group-content";
    contentEl.id = `input_group_content_${groupId}`;
    contentEl.appendChild(content);

    group.appendChild(contentEl);
    return group;
};

export const wrapForm = (
    groupId: string,
    content: HTMLElement,
): HTMLElement => {
    const body = document.createElement("div");
    body.className = "vst-detail-body";
    body.appendChild(buildGroup(groupId, content));
    return body;
};

/**
 * Tag a field's inner control with a focus key so the strip can preserve and
 * restore its caret across a self-triggered rebuild.
 */
export const tagFocus = (field: HTMLElement, key: string): HTMLElement => {
    const control =
        field.querySelector<HTMLElement>("input.auto-slider-number") ??
        field.querySelector<HTMLElement>("input, select") ??
        (field.matches("input, select") ? field : null);
    control?.setAttribute("data-vst-focus-key", key);
    return field;
};

/**
 * The `wrap.vst-detail-stages-wrap` > [label, `col`] skeleton shared by the
 * clip panel's Retake and IC-LoRA sections: a plain section label above a
 * column that the caller fills with per-entry rows and a trailing Add button.
 */
export const buildStackSection = (
    label: string,
    colClass: string,
): { wrap: HTMLElement; col: HTMLElement } => {
    const wrap = document.createElement("div");
    wrap.className = "vst-detail-stages-wrap";
    wrap.appendChild(sectionLabel(label));
    const col = document.createElement("div");
    col.className = `vst-detail-col ${colClass}`;
    wrap.appendChild(col);
    return { wrap, col };
};
