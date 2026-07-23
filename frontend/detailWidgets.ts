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
    row.className = "auto-input vst-audio-field";
    const boxClass = boxClassFor(control);
    if (boxClass) {
        row.classList.add(boxClass);
    }
    const labelEl = document.createElement("label");
    const text = document.createElement("span");
    text.className = "auto-input-name vst-audio-field-label";
    text.textContent = label;
    labelEl.appendChild(text);
    if (help) {
        appendHelp(labelEl, row, label, help);
    }
    row.append(labelEl, control);
    if (hint) {
        const small = document.createElement("small");
        small.className = "vst-audio-field-hint";
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
        small.className = "vst-audio-field-hint";
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
        "auto-input auto-checkbox-box auto-input-flex vst-audio-field vst-audio-field-check";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.className = "auto-checkbox";
    input.checked = checked;
    input.addEventListener("change", () => onChange(input.checked));
    const text = document.createElement("span");
    text.className = "auto-input-name vst-audio-field-label";
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
    row.className = "auto-input vst-audio-field vst-audio-upload";
    const pickLabel = document.createElement("span");
    pickLabel.className = "auto-input-name vst-audio-field-label";
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

export interface InstanceRowSpec {
    rowClass: string;
    indexAttr: string;
    index: number;
    active: boolean;
    title: string;
    deleteLabel: string;
    onDelete: () => void;
    repoint: () => void;
}

/**
 * One instance of a "clip has multiples of a thing" panel (a ref, an audio
 * segment) rendered as a stacked, individually-editable sub-section. Shared
 * machinery matching the relay list: a `R{n}`/`S{n}` title, a per-row delete,
 * an active highlight, and a focusin re-point so touching any control makes
 * this instance the selection (timeline highlight follows) — targeted swap,
 * no rebuild. Returns the row and the field-body to append controls into.
 */
export const buildInstanceRow = (
    spec: InstanceRowSpec,
): { row: HTMLElement; fields: HTMLElement } => {
    const row = document.createElement("div");
    row.className = `vst-detail-instance ${spec.rowClass}`;
    row.setAttribute(spec.indexAttr, `${spec.index}`);
    if (spec.active) {
        row.classList.add("vst-detail-instance-active");
    }
    const head = document.createElement("div");
    head.className = "vst-detail-instance-head";
    const title = document.createElement("span");
    title.className = "vst-detail-instance-title";
    title.textContent = spec.title;
    const del = document.createElement("button");
    del.type = "button";
    del.className =
        "basic-button small-button vst-refs-delete vst-detail-delete vst-detail-instance-delete";
    del.textContent = spec.deleteLabel;
    del.title = spec.deleteLabel;
    del.addEventListener("click", (event) => {
        event.preventDefault();
        spec.onDelete();
    });
    head.append(title, del);
    row.appendChild(head);
    const fields = document.createElement("div");
    fields.className = "vst-detail-instance-fields";
    row.appendChild(fields);
    row.addEventListener("focusin", () => spec.repoint());
    return { row, fields };
};

export const sectionLabel = (text: string): HTMLElement => {
    const sec = document.createElement("div");
    sec.className = "vst-detail-sec vst-detail-wrap-sec";
    sec.textContent = text;
    return sec;
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

/** Trailing "Add" button for a stacked section. */
export const buildAddButton = (
    label: string,
    extraClass: string,
    onClick: () => void,
): HTMLButtonElement => {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = `basic-button small-button vst-detail-rail-btn ${extraClass}`;
    btn.textContent = label;
    btn.addEventListener("click", (event) => {
        event.preventDefault();
        onClick();
    });
    return btn;
};
