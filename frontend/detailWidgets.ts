import { clamp } from "./constants";

let sliderSeq = 0;

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

export const buildSelect = (
    values: string[],
    labels: string[],
    selected: string,
    onChange: (value: string) => void,
): HTMLSelectElement => {
    const select = document.createElement("select");
    select.className = "auto-dropdown vst-audio-select";
    for (let i = 0; i < values.length; i++) {
        const opt = document.createElement("option");
        opt.value = values[i];
        opt.textContent = labels[i] ?? values[i];
        opt.dataset.cleanname = labels[i] ?? values[i];
        opt.selected = values[i] === selected;
        select.appendChild(opt);
    }
    select.addEventListener("change", () => onChange(select.value));
    return select;
};

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
    const apply = (normalize: boolean): void => {
        const parsed = Number.parseFloat(input.value);
        const next = clamp(Number.isFinite(parsed) ? parsed : value, min, max);
        onChange(next);
        if (normalize) {
            input.value = `${next}`;
        }
    };
    input.addEventListener("input", () => apply(false));
    input.addEventListener("change", () => apply(true));
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
        const apply = (normalize: boolean): void => {
            const parsed = Number.parseFloat(number.value);
            const next = clamp(
                Number.isFinite(parsed) ? parsed : value,
                min,
                max,
            );
            onChange(next);
            if (normalize) {
                number.value = `${next}`;
            }
        };
        number.addEventListener("input", () => apply(false));
        number.addEventListener("change", () => apply(true));
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
    return row;
};
