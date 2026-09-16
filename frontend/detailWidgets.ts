import { storeBrowserMedia } from "./browserMediaStore";
import { clamp, mediaPreviewSrc } from "./constants";
import {
    buildSidebarMediaPreview,
    type MediaPreviewPlayback,
    makeMediaPreviewExpandable,
} from "./detailStrip/sidebarMediaPreview";
import { fileAsDataUri } from "./fileDataUri";
import { hasDroppedFiles } from "./fileDrop";
import { getVideoStagesHostBridge } from "./host";
import {
    enhanceHostPromptEditor,
    hasHostInputBrowser,
    isHostReusableMediaPath,
    openHostInputBrowser,
    renderHostSlider,
    restoreHostMediaPreview,
    showHostPopover,
} from "./host/swarmUiAdapters";
import { buildToggleSwitch, type ToggleSwitchOptions } from "./toggleSwitch";

let sliderSeq = 0;
let helpSeq = 0;
let fieldSeq = 0;

const slugify = (value: string): string =>
    value
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, "-")
        .replace(/(^-|-$)/g, "") || "field";

/** Host-compatible info popover with a text-only body. */
export const appendHelp = (
    labelEl: HTMLElement,
    row: HTMLElement | DocumentFragment,
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
    labelEl.insertBefore(btn, labelEl.firstChild);
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

const wireUnboundedNumericInput = (
    input: HTMLInputElement,
    fallback: number,
    onChange: (value: number) => void,
): void => {
    const apply = (normalize: boolean): void => {
        const parsed = Number.parseFloat(input.value);
        const next = Number.isFinite(parsed) ? parsed : fallback;
        onChange(next);
        if (normalize) {
            input.value = `${next}`;
        }
    };
    input.removeAttribute("min");
    input.removeAttribute("max");
    input.addEventListener("input", () => apply(false));
    input.addEventListener("change", () => apply(true));
};

const boxClassFor = (control: HTMLElement): string | null => {
    if (control.classList.contains("auto-checkbox")) {
        return "auto-checkbox-box";
    }
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

const buildFieldLabel = (label: string): HTMLLabelElement => {
    const labelEl = document.createElement("label");
    const text = document.createElement("span");
    text.className = "auto-input-name vst-detail-field-label";
    text.textContent = label;
    labelEl.appendChild(text);
    return labelEl;
};

export const buildField = (
    label: string,
    control: HTMLElement,
    hint?: string,
    help?: string,
): HTMLElement => {
    const row = document.createElement("div");
    row.className = "auto-input vst-detail-field vst-form-field";
    row.dataset.fieldKind =
        control instanceof HTMLInputElement
            ? control.type
            : control.tagName.toLowerCase();
    row.classList.add(
        control.classList.contains("auto-text-block")
            ? "auto-input-flex-wide"
            : "auto-input-flex",
    );
    const boxClass = boxClassFor(control);
    if (boxClass) {
        row.classList.add(boxClass);
    }
    const labelEl = buildFieldLabel(label);
    if (
        control instanceof HTMLInputElement ||
        control instanceof HTMLSelectElement ||
        control instanceof HTMLTextAreaElement
    ) {
        if (!control.id) {
            control.id = `vst_field_${slugify(label)}_${++fieldSeq}`;
        }
        labelEl.htmlFor = control.id;
    }
    row.append(labelEl, control);
    if (help) {
        appendHelp(labelEl, row, label, help);
    }
    if (hint) {
        const small = document.createElement("small");
        small.className = "auto-input-description vst-detail-field-hint";
        small.textContent = hint;
        row.appendChild(small);
    }
    return row;
};

export const setFieldDisabled = (
    field: HTMLElement,
    disabled: boolean,
    reason = "",
): void => {
    field
        .querySelectorAll<
            | HTMLInputElement
            | HTMLSelectElement
            | HTMLTextAreaElement
            | HTMLButtonElement
        >("input, select, textarea, button")
        .forEach((control) => {
            control.disabled = disabled;
        });
    field.classList.toggle("vst-field-disabled", disabled);
    field.title = disabled ? reason : "";
};

export interface OptionSpec {
    value: string;
    label: string;
    disabled?: boolean;
}

export const buildOptionSelect = (
    specs: OptionSpec[],
    selected: string,
    onChange?: (value: string) => void,
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
    if (onChange) {
        select.addEventListener("change", () => onChange(select.value));
    }
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
    wireNumericInput(input, value, min, max, onChange);
    return input;
};

export const buildUnboundedNumber = (
    value: number,
    step: number,
    onChange: (value: number) => void,
): HTMLInputElement => {
    const input = document.createElement("input");
    input.type = "number";
    input.className = "auto-number vst-refs-num";
    input.step = `${step}`;
    input.value = `${value}`;
    wireUnboundedNumericInput(input, value, onChange);
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
        allowNumberOutOfRange?: boolean;
        isPot?: boolean;
    },
): HTMLElement => {
    const holder = document.createElement("div");
    holder.className =
        "vst-stage-slider auto-input-flex-wide vst-detail-field vst-form-field";
    holder.dataset.fieldKind = "slider";
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
        isPot: opts?.isPot,
    });
    holder
        .querySelector(".auto-input-name")
        ?.classList.add("vst-detail-field-label");
    const number = holder.querySelector<HTMLInputElement>(
        "input.auto-slider-number",
    );
    if (number) {
        const labelEl = holder.querySelector<HTMLLabelElement>("label");
        if (labelEl) {
            labelEl.htmlFor = number.id;
            labelEl.addEventListener("click", (event) => {
                if (
                    (event.target as HTMLElement).closest(
                        "input, .info-popover-button",
                    )
                )
                    return;
                event.preventDefault();
                if (!number.disabled) {
                    number.focus();
                    number.select();
                }
            });
        }
        number.step = `${opts?.numberStep ?? step}`;
        if (opts?.allowNumberOutOfRange) {
            wireUnboundedNumericInput(number, value, onChange);
        } else {
            wireNumericInput(number, value, min, max, onChange);
        }
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
    const input = document.createElement("input");
    input.type = "checkbox";
    input.className = "auto-checkbox";
    input.dataset.name = label;
    input.checked = checked;
    input.addEventListener("change", () => onChange(input.checked));
    const row = buildField(label, input, undefined, opts?.help);
    row.classList.add("vst-detail-field-check");
    row.dataset.disabled = `${opts?.disabled === true}`;
    if (opts?.disabled) {
        row.classList.add("vst-audio-disabled");
        input.disabled = true;
    }
    return row;
};

export interface TextareaLayout {
    rows: number;
    maxRows?: number;
}

export const buildTextarea = (
    value: string,
    placeholder: string,
    focusKey: string,
    onInput: (value: string) => void,
    layout?: TextareaLayout,
): HTMLTextAreaElement => {
    const editor = document.createElement("textarea");
    editor.className =
        "auto-text auto-text-block vst-prompt-editor vst-detail-prompt";
    editor.value = value;
    editor.placeholder = placeholder;
    editor.setAttribute("data-vst-focus-key", focusKey);
    if (layout) {
        editor.rows = layout.rows;
        if (layout.maxRows !== undefined) {
            editor.classList.add("vst-textarea-limited");
            editor.style.setProperty(
                "--vst-textarea-max-lines",
                `${layout.maxRows}lh`,
            );
        }
    }
    editor.addEventListener("input", () => onInput(editor.value));
    enhanceHostPromptEditor(editor);
    return editor;
};

let mediaPickCounter = 0;

/** Host server picks arrive through `dataset.filedata`. */
export const buildMediaPickRow = (
    label: string,
    accept: string,
    browserTypes: string[],
    name: string | null | undefined,
    onFile: (data: string, fileName: string) => void,
    onClear: () => void,
    previewData?: string | null,
    playback?: MediaPreviewPlayback,
): HTMLElement => {
    const row = document.createElement("div");
    row.className =
        "auto-input auto-file-box vst-detail-field vst-form-field vst-audio-upload";
    row.dataset.fieldKind = "file";
    const controls = document.createElement("div");
    controls.className = "auto-file-input-label";
    const pickLabel = document.createElement("span");
    pickLabel.className = "auto-input-name vst-detail-field-label";
    pickLabel.textContent = label;
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.className = "auto-file";
    fileInput.accept = accept;
    fileInput.id = `vst-media-pick-${++mediaPickCounter}`;
    const uploadBtn = document.createElement("button");
    uploadBtn.type = "button";
    uploadBtn.className =
        "basic-button auto-file-input-button vst-media-pick-upload";
    uploadBtn.textContent = "Upload";
    uploadBtn.addEventListener("click", () => fileInput.click());
    const actions = document.createElement("span");
    actions.className = "vst-media-pick-actions";
    actions.appendChild(uploadBtn);
    controls.append(pickLabel, actions);
    const fileDrop = document.createElement("label");
    fileDrop.className = "auto-file-label";
    fileDrop.htmlFor = fileInput.id;
    const fileDisplay = document.createElement("div");
    fileDisplay.className = "auto-file-input";
    const fileName = document.createElement("span");
    fileName.className = "auto-file-input-filename vst-audio-upload-name";
    fileName.textContent = name ? name : "No file chosen";
    fileDisplay.appendChild(fileName);
    fileDrop.append(fileInput, fileDisplay);
    const preview = document.createElement("div");
    preview.className = "auto-input-preview";
    const clearBtn = document.createElement("button");
    clearBtn.type = "button";
    clearBtn.className =
        "basic-button auto-file-input-button vst-audio-upload-clear";
    clearBtn.textContent = "Clear";
    clearBtn.hidden = !name;
    const acceptFile = (file: File): void => {
        void fileAsDataUri(file).then((data) => {
            if (data) {
                onFile(storeBrowserMedia(data), file.name);
            }
        });
    };
    const previewSrc = previewData ? mediaPreviewSrc(previewData) : "";
    fileInput.addEventListener("change", (event) => {
        const file = fileInput.files?.[0];
        if (file) {
            acceptFile(file);
            return;
        }
        const picked = fileInput.dataset.filedata ?? "";
        // Core emits change after a restored preview loads.
        if (previewSrc && picked === previewSrc) {
            event.stopPropagation();
            return;
        }
        if (!picked) {
            if (previewSrc) onClear();
            return;
        }
        const pickedName = fileInput.dataset.filename ?? "server file";
        if (picked.startsWith("data:")) {
            onFile(storeBrowserMedia(picked), pickedName);
            return;
        }
        if (isHostReusableMediaPath(picked)) {
            onFile(picked, pickedName);
            return;
        }
        void getVideoStagesHostBridge()
            .toDataUrl(picked)
            .then((data) => onFile(storeBrowserMedia(data), pickedName));
    });
    row.addEventListener("dragover", (event) => {
        if (!hasDroppedFiles(event.dataTransfer)) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        if (event.dataTransfer) {
            event.dataTransfer.dropEffect = "copy";
        }
    });
    row.addEventListener("drop", (event) => {
        const file = event.dataTransfer?.files[0];
        if (!file) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        acceptFile(file);
    });
    clearBtn.addEventListener("click", () => onClear());
    if (hasHostInputBrowser()) {
        const selectBtn = document.createElement("button");
        selectBtn.type = "button";
        selectBtn.className =
            "basic-button auto-file-input-button vst-media-pick-select";
        selectBtn.textContent = "Select";
        selectBtn.addEventListener("click", () =>
            openHostInputBrowser(fileInput.id, browserTypes),
        );
        actions.appendChild(selectBtn);
    }
    actions.appendChild(clearBtn);
    const details = document.createElement("div");
    details.className = "vst-media-pick-details";
    details.append(controls, fileDrop);
    row.append(preview, details);
    if (previewSrc) {
        if (playback) {
            fileInput.dataset.filedata = previewSrc;
            preview.append(
                buildSidebarMediaPreview(
                    playback.kind,
                    previewData ?? previewSrc,
                    playback.range,
                ),
            );
        } else {
            restoreHostMediaPreview(fileInput, previewSrc, name || "media");
        }
        makeMediaPreviewExpandable(
            preview,
            previewData ?? previewSrc,
            name || "media",
            playback,
        );
    }
    row.classList.toggle(
        "vst-media-pick-has-preview",
        Boolean(preview.firstChild),
    );
    return row;
};

const setAccordionOpen = (section: HTMLElement, open: boolean): void => {
    const header = section.querySelector<HTMLElement>(
        ":scope > .input-group-header",
    );
    const content = section.querySelector<HTMLElement>(
        ":scope > .input-group-content",
    );
    const symbol = header?.querySelector<HTMLElement>(".auto-symbol");
    section.classList.toggle("input-group-open", open);
    section.classList.toggle("input-group-closed", !open);
    header?.setAttribute("aria-expanded", `${open}`);
    if (content) {
        content.style.removeProperty("display");
        content.hidden = !open;
        if (
            section.classList.contains("vst-detail-repeating-group") &&
            content.childNodes.length > 0
        ) {
            content.classList.toggle(
                "vst-detail-repeating-editor-active",
                open,
            );
        }
    }
    if (symbol) {
        symbol.textContent = open ? "⮟" : "⮞";
    }
};

const appendSectionContent = (
    target: HTMLElement,
    source: HTMLElement | DocumentFragment,
    flatten: boolean,
): void => {
    if (!flatten || source instanceof DocumentFragment) {
        target.appendChild(source);
        return;
    }
    for (const { name, value } of Array.from(source.attributes)) {
        if (name.startsWith("data-")) {
            target.setAttribute(name, value);
        }
    }
    target.classList.add(...Array.from(source.classList));
    target.append(...Array.from(source.childNodes));
};

export interface AccordionSectionSpec {
    key: string;
    label: string;
    content: HTMLElement | DocumentFragment;
    open?: boolean;
    defaultOpen?: boolean;
    counter?: string | number;
    className?: string;
    flattenContent?: boolean;
    headerAction?: SectionHeaderAction;
    headerActions?: SectionHeaderAction[];
    toggle?: ToggleSwitchOptions;
}

export interface SectionHeaderAction {
    label: string;
    title: string;
    className?: string;
    active?: boolean;
    variant?: "basic" | "interrupt";
    onClick: () => void;
}

export interface DetailActionButtonSpec {
    label: string;
    title: string;
    className: string;
    variant?: "basic" | "interrupt";
    active?: boolean;
    disabled?: boolean;
    stopPropagation?: boolean;
    onClick: (button: HTMLButtonElement) => void;
}

export const buildDetailActionButton = (
    spec: DetailActionButtonSpec,
): HTMLButtonElement => {
    const button = document.createElement("button");
    button.type = "button";
    button.className =
        `${spec.variant === "interrupt" ? "interrupt-button" : "basic-button"} ${spec.className}`.trim();
    button.textContent = spec.label;
    button.title = spec.title;
    button.setAttribute("aria-label", spec.title);
    button.disabled = spec.disabled === true;
    if (spec.active !== undefined) {
        button.setAttribute("aria-pressed", `${spec.active}`);
        button.classList.toggle("vst-btn-skip-active", spec.active);
    }
    button.addEventListener("click", (event) => {
        event.preventDefault();
        if (spec.stopPropagation) {
            event.stopPropagation();
        }
        spec.onClick(button);
    });
    return button;
};

const appendSectionHeaderAction = (
    target: HTMLElement,
    actionSpec: SectionHeaderAction,
): void => {
    const action = buildDetailActionButton({
        label: actionSpec.label,
        title: actionSpec.title,
        className:
            `vst-btn-tiny vst-detail-repeating-group-action ${actionSpec.className ?? ""}`.trim(),
        variant: actionSpec.variant,
        active: actionSpec.active,
        stopPropagation: true,
        onClick: actionSpec.onClick,
    });
    target.appendChild(action);
};

const appendSectionHeaderActions = (
    actions: HTMLElement,
    spec: {
        headerAction?: SectionHeaderAction;
        headerActions?: SectionHeaderAction[];
    },
): void => {
    const headerActions =
        spec.headerActions ??
        (spec.headerAction === undefined ? [] : [spec.headerAction]);
    if (headerActions.length === 0) {
        return;
    }
    for (const action of headerActions) {
        appendSectionHeaderAction(actions, action);
    }
};

interface SectionHeaderSpec {
    label: string;
    className: string;
    open?: boolean;
    counter?: string | number;
}

const buildSectionHeader = (
    spec: SectionHeaderSpec,
): {
    header: HTMLSpanElement;
    heading: HTMLSpanElement;
    actions: HTMLSpanElement;
} => {
    const header = document.createElement("span");
    header.className = spec.className;
    const labelWrap = document.createElement("span");
    labelWrap.className = "header-label-wrap";
    if (spec.open !== undefined) {
        const symbol = document.createElement("span");
        symbol.className = "auto-symbol";
        symbol.textContent = spec.open ? "⮟" : "⮞";
        labelWrap.appendChild(symbol);
    }
    const heading = document.createElement("span");
    heading.className = "header-label";
    heading.textContent = spec.label;
    const spacer = document.createElement("span");
    spacer.className = "header-label-spacer";
    labelWrap.append(heading, spacer);
    if (spec.counter !== undefined) {
        const counter = document.createElement("span");
        counter.className = "header-label-counter";
        counter.textContent = `${spec.counter}`;
        labelWrap.appendChild(counter);
    }
    const actions = document.createElement("span");
    actions.className = "vst-detail-repeating-group-actions";
    labelWrap.appendChild(actions);
    header.appendChild(labelWrap);
    return { header, heading, actions };
};

const rememberedAccordionSections = new Set<string>();
const seenAccordionSections = new Set<string>();

export const resetRememberedAccordionSections = (): void => {
    rememberedAccordionSections.clear();
    seenAccordionSections.clear();
};

/** Remembers expansion independently of the feature's enabled state. */
export const buildAccordionSection = (
    spec: AccordionSectionSpec,
): {
    section: HTMLElement;
    heading: HTMLElement;
    content: HTMLElement;
} => {
    const firstBuild = !seenAccordionSections.has(spec.key);
    const open =
        spec.open === true ||
        (firstBuild && spec.defaultOpen === true) ||
        rememberedAccordionSections.has(spec.key);
    seenAccordionSections.add(spec.key);
    if (open) {
        rememberedAccordionSections.add(spec.key);
    }
    const section = document.createElement("div");
    section.className =
        `input-group vst-detail-section ${open ? "input-group-open" : "input-group-closed"} ${spec.className ?? ""}`.trim();
    section.classList.toggle(
        "vst-detail-section-has-toggle",
        spec.toggle !== undefined,
    );
    section.dataset.vstAccordionKey = spec.key;

    const { header, heading, actions } = buildSectionHeader({
        label: spec.label,
        className:
            "input-group-header input-group-shrinkable vst-detail-section-header",
        open,
        counter: spec.counter,
    });
    header.tabIndex = 0;
    header.setAttribute("role", "button");
    header.setAttribute("aria-expanded", `${open}`);
    appendSectionHeaderActions(actions, spec);
    if (spec.toggle) {
        heading.after(buildToggleSwitch(spec.toggle));
    }

    const content = document.createElement("fieldset");
    content.className = "input-group-content vst-detail-section-content";
    content.disabled = spec.toggle?.checked === false;
    content.classList.toggle("vst-field-disabled", content.disabled);
    content.hidden = !open;
    appendSectionContent(content, spec.content, spec.flattenContent === true);

    const toggle = (event: Event): void => {
        event.preventDefault();
        event.stopPropagation();
        const opening = content.hidden === true;
        setAccordionOpen(section, opening);
        if (opening) {
            rememberedAccordionSections.add(spec.key);
        } else {
            rememberedAccordionSections.delete(spec.key);
        }
    };
    header.addEventListener("click", toggle);
    header.addEventListener("keydown", (event) => {
        if (
            event.target === header &&
            (event.key === "Enter" || event.key === " ")
        ) {
            toggle(event);
        }
    });
    section.append(header, content);
    return { section, heading, content };
};

export const tagFocus = <T extends HTMLElement>(field: T, key: string): T => {
    const control =
        field.querySelector<HTMLElement>("input.auto-slider-number") ??
        field.querySelector<HTMLElement>("input, select") ??
        (field.matches("input, select") ? field : null);
    control?.setAttribute("data-vst-focus-key", key);
    return field;
};
