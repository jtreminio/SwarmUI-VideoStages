import { clamp } from "./constants";

let sliderSeq = 0;

/**
 * Shared parse→clamp→onChange wiring for a numeric input: live `input` events
 * commit without rewriting the field (so typing isn't fought), `change`
 * normalizes the display back to the clamped value.
 */
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

/**
 * Pick the host `.auto-*-box` wrapper modifier that matches a control, so the
 * field row reads as a real SwarmUI `.auto-input` widget. Composite controls
 * (dims pair, sliders) carry no native control class and get no box modifier.
 */
const boxClassFor = (control: HTMLElement): string | null => {
    const cl = control.classList;
    if (cl.contains("auto-dropdown")) {
        return "auto-dropdown-box";
    }
    if (cl.contains("auto-number")) {
        return "auto-number-box";
    }
    if (cl.contains("auto-text")) {
        return "auto-text-box";
    }
    return null;
};

/**
 * Label + control row in SwarmUI's native `.auto-input` vocabulary: the wrapper
 * is `.auto-input` (host-styled), the label is `.auto-input-name` inside a
 * `<label>`, and the control keeps its native `.auto-*` class. The legacy
 * `.vst-audio-field` / `.vst-audio-field-label` classes ride along as hooks for
 * the focus/pending machinery and the tests — they no longer carry styling.
 */
export const buildField = (
    label: string,
    control: HTMLElement,
    hint?: string,
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

/** Parallel-arrays convenience form of buildOptionSelect. */
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
    opts?: { hint?: string; title?: string },
): HTMLElement => {
    const holder = document.createElement("div");
    holder.className = "vst-stage-slider";
    const id = `vst_stage_slider_${++sliderSeq}`;
    holder.innerHTML = makeSliderInput(
        null,
        id,
        "",
        label,
        "",
        value,
        min,
        max,
        min,
        max,
        step,
        false,
        false,
        false,
    );
    const number = holder.querySelector<HTMLInputElement>(
        "input.auto-slider-number",
    );
    if (number) {
        wireNumericInput(number, value, min, max, onChange);
    }
    if (opts?.title) {
        holder.title = opts.title;
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
    opts?: { disabled?: boolean },
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
    if (typeof textPromptAddKeydownHandler === "function") {
        textPromptAddKeydownHandler(editor);
    }
    return editor;
};

export const buildUploadRow = (
    label: string,
    accept: string,
    name: string | null | undefined,
    onFile: (data: string, fileName: string) => void,
    onClear: () => void,
): HTMLElement => {
    const row = document.createElement("div");
    row.className = "auto-input vst-audio-field vst-audio-upload";
    const uploadLabel = document.createElement("span");
    uploadLabel.className = "auto-input-name vst-audio-field-label";
    uploadLabel.textContent = label;
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = accept;
    const fileName = document.createElement("span");
    fileName.className = "vst-audio-upload-name";
    fileName.textContent = name ? name : "No file chosen";
    const clearBtn = document.createElement("button");
    clearBtn.type = "button";
    clearBtn.className = "basic-button small-button vst-audio-upload-clear";
    clearBtn.textContent = "Clear";
    clearBtn.hidden = !name;
    fileInput.addEventListener("change", () => {
        const file = fileInput.files?.[0];
        if (!file) {
            return;
        }
        const reader = new FileReader();
        reader.onload = () => {
            const data = `${reader.result ?? ""}`;
            if (data) {
                onFile(data, file.name);
            }
        };
        reader.readAsDataURL(file);
    });
    clearBtn.addEventListener("click", () => onClear());
    row.append(uploadLabel, fileInput, fileName, clearBtn);
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
    // Interacting with any control in this row re-points the selection so the
    // timeline highlight follows. setSelection no-ops on an identical
    // selection, so this never interrupts an in-progress edit.
    row.addEventListener("focusin", () => spec.repoint());
    return { row, fields };
};

/** Plain section label used between the strip's stacked sections. */
export const sectionLabel = (text: string): HTMLElement => {
    const sec = document.createElement("div");
    sec.className = "vst-detail-sec vst-detail-wrap-sec";
    sec.textContent = text;
    return sec;
};
