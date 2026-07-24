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
let checkboxSeq = 0;

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
    row.className = "auto-input vst-detail-field";
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
    if (help) {
        appendHelp(labelEl, row, label, help);
    }
    row.append(labelEl, control);
    if (hint) {
        const small = document.createElement("small");
        small.className = "auto-input-description vst-detail-field-hint";
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
    },
): HTMLElement => {
    const holder = document.createElement("div");
    holder.className = "vst-stage-slider auto-input-flex-wide";
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
    const row = document.createElement("div");
    row.className =
        "auto-input auto-checkbox-box auto-input-flex vst-detail-field vst-detail-field-check";
    row.dataset.disabled = `${opts?.disabled === true}`;
    const input = document.createElement("input");
    input.type = "checkbox";
    input.className = "auto-checkbox";
    input.id = `vst_checkbox_${slugify(label)}_${++checkboxSeq}`;
    input.dataset.name = label;
    input.checked = checked;
    input.addEventListener("change", () => onChange(input.checked));
    const labelEl = buildFieldLabel(label);
    row.append(labelEl, input);
    if (opts?.help) {
        appendHelp(labelEl, row, label, opts.help);
    }
    if (opts?.disabled) {
        row.classList.add("vst-audio-disabled");
        input.disabled = true;
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
    row.className =
        "auto-input auto-file-box vst-detail-field vst-audio-upload";
    const controls = document.createElement("label");
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
    controls.append(pickLabel, uploadBtn);
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
    if (hasHostInputBrowser()) {
        const selectBtn = document.createElement("button");
        selectBtn.type = "button";
        selectBtn.className =
            "basic-button auto-file-input-button vst-media-pick-select";
        selectBtn.textContent = "Select";
        selectBtn.addEventListener("click", () =>
            openHostInputBrowser(fileInput.id, browserTypes),
        );
        controls.appendChild(selectBtn);
    }
    controls.appendChild(clearBtn);
    row.append(controls, fileDrop, preview);
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
    if (section.classList.contains("vst-detail-repeating-group")) {
        header?.setAttribute("aria-pressed", `${open}`);
    }
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

const closeSiblingAccordionSections = (section: HTMLElement): void => {
    const parent = section.parentElement;
    if (!parent) {
        return;
    }
    for (const sibling of parent.children) {
        if (
            sibling instanceof HTMLElement &&
            sibling !== section &&
            sibling.classList.contains("vst-detail-section")
        ) {
            setAccordionOpen(sibling, false);
        }
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
    counter?: string | number;
    className?: string;
    flattenContent?: boolean;
}

export interface SectionHeaderAction {
    label: string;
    title: string;
    className?: string;
    active?: boolean;
    onClick: () => void;
}

const appendSectionHeaderAction = (
    target: HTMLElement,
    actionSpec: SectionHeaderAction,
): void => {
    const action = document.createElement("button");
    action.type = "button";
    action.className =
        `basic-button vst-btn-tiny vst-detail-repeating-group-action ${actionSpec.className ?? ""}`.trim();
    action.textContent = actionSpec.label;
    action.title = actionSpec.title;
    action.setAttribute("aria-label", actionSpec.title);
    action.setAttribute("aria-pressed", `${actionSpec.active === true}`);
    action.classList.toggle("vst-btn-skip-active", actionSpec.active === true);
    action.addEventListener("click", (event) => {
        event.preventDefault();
        event.stopPropagation();
        actionSpec.onClick();
    });
    target.appendChild(action);
};

export interface StaticSectionSpec {
    key: string;
    label: string;
    content: HTMLElement | DocumentFragment;
    className?: string;
    flattenContent?: boolean;
    headerAction?: SectionHeaderAction;
}

/**
 * Native SwarmUI input group that is always open. It keeps the same header and
 * content structure as an accordion group, but deliberately omits the
 * shrinkable class, disclosure symbol, button role, and toggle listeners.
 */
export const buildStaticSection = (
    spec: StaticSectionSpec,
): {
    section: HTMLElement;
    heading: HTMLElement;
    content: HTMLElement;
} => {
    const section = document.createElement("div");
    section.className =
        `input-group input-group-open vst-detail-section vst-detail-static-section ${spec.className ?? ""}`.trim();
    section.dataset.vstStaticKey = spec.key;

    const header = document.createElement("span");
    header.className =
        "input-group-header input-group-noshrink vst-detail-section-header";
    const labelWrap = document.createElement("span");
    labelWrap.className = "header-label-wrap";
    const heading = document.createElement("span");
    heading.className = "header-label";
    heading.textContent = spec.label;
    const spacer = document.createElement("span");
    spacer.className = "header-label-spacer";
    labelWrap.append(heading, spacer);
    if (spec.headerAction) {
        const actions = document.createElement("span");
        actions.className = "vst-detail-repeating-group-actions";
        appendSectionHeaderAction(actions, spec.headerAction);
        labelWrap.appendChild(actions);
    }
    header.appendChild(labelWrap);

    const content = document.createElement("div");
    content.className = "input-group-content vst-detail-section-content";
    appendSectionContent(content, spec.content, spec.flattenContent === true);
    section.append(header, content);
    return { section, heading, content };
};

/**
 * Native SwarmUI sidebar group with a wired shrinkable header. Sibling
 * VideoStages sections form a progressive accordion: opening one closes the
 * others in the same panel. Geometry and field chrome are left to the host
 * `.input-group` styles.
 */
export const buildAccordionSection = (
    spec: AccordionSectionSpec,
): {
    section: HTMLElement;
    heading: HTMLElement;
    content: HTMLElement;
} => {
    const section = document.createElement("div");
    section.className =
        `input-group vst-detail-section ${spec.open ? "input-group-open" : "input-group-closed"} ${spec.className ?? ""}`.trim();
    section.dataset.vstAccordionKey = spec.key;

    const header = document.createElement("span");
    header.className =
        "input-group-header input-group-shrinkable vst-detail-section-header";
    header.tabIndex = 0;
    header.setAttribute("role", "button");
    header.setAttribute("aria-expanded", `${spec.open === true}`);

    const labelWrap = document.createElement("span");
    labelWrap.className = "header-label-wrap";
    const symbol = document.createElement("span");
    symbol.className = "auto-symbol";
    symbol.textContent = spec.open ? "⮟" : "⮞";
    const heading = document.createElement("span");
    heading.className = "header-label";
    heading.textContent = spec.label;
    const spacer = document.createElement("span");
    spacer.className = "header-label-spacer";
    labelWrap.append(symbol, heading, spacer);
    if (spec.counter !== undefined) {
        const counter = document.createElement("span");
        counter.className = "header-label-counter";
        counter.textContent = `${spec.counter}`;
        labelWrap.appendChild(counter);
    }
    header.appendChild(labelWrap);

    const content = document.createElement("div");
    content.className = "input-group-content vst-detail-section-content";
    content.hidden = spec.open !== true;
    appendSectionContent(content, spec.content, spec.flattenContent === true);

    const toggle = (event: Event): void => {
        event.preventDefault();
        event.stopPropagation();
        const opening = content.hidden === true;
        if (opening) {
            closeSiblingAccordionSections(section);
        }
        setAccordionOpen(section, opening);
    };
    header.addEventListener("click", toggle);
    header.addEventListener("keydown", (event) => {
        if (event.key === "Enter" || event.key === " ") {
            toggle(event);
        }
    });
    section.append(header, content);
    return { section, heading, content };
};

export interface RepeatingGroupItem {
    label: string;
    focusKey?: string;
    title?: string;
    active?: boolean;
    className?: string;
    groupClassName?: string;
    editor?: HTMLElement;
    onSelect?: () => void;
    onShiftDelete?: () => void;
    onDelete?: () => void;
    deleteTitle?: string;
    deleteDisabled?: boolean;
    headerAction?: SectionHeaderAction;
}

export interface RepeatingGroupAddAction {
    title: string;
    className: string;
    label?: string;
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
    open?: boolean;
    defaultActiveIndex?: number | null;
}

const rememberedRepeaterItems = new Map<string, number>();
const forceOpenRepeaterKeys = new Set<string>();

/**
 * Canonical repeating-child section: one native outer SwarmUI accordion group,
 * containing one native group per item and the extension's Add action.
 */
export const buildRepeatingEditor = (
    spec: RepeatingEditorSpec,
): {
    section: HTMLElement;
    heading: HTMLElement;
    content: HTMLElement;
    editor: HTMLElement | null;
} => {
    const explicitActiveIndex = spec.items.findIndex(
        (item) => item.active === true,
    );
    const rememberedIndex = rememberedRepeaterItems.get(spec.key);
    const validRememberedIndex =
        rememberedIndex !== undefined &&
        rememberedIndex >= 0 &&
        rememberedIndex < spec.items.length
            ? rememberedIndex
            : null;
    if (rememberedIndex !== undefined && validRememberedIndex === null) {
        rememberedRepeaterItems.delete(spec.key);
        forceOpenRepeaterKeys.delete(spec.key);
    }
    const forceOpen =
        forceOpenRepeaterKeys.has(spec.key) && validRememberedIndex !== null;
    const activeIndex = forceOpen
        ? validRememberedIndex
        : explicitActiveIndex >= 0
          ? explicitActiveIndex
          : (validRememberedIndex ?? spec.defaultActiveIndex ?? null);
    if (explicitActiveIndex >= 0 && !forceOpen) {
        rememberedRepeaterItems.set(spec.key, explicitActiveIndex);
    } else if (activeIndex !== null) {
        rememberedRepeaterItems.set(spec.key, activeIndex);
    }
    if (forceOpen) {
        forceOpenRepeaterKeys.delete(spec.key);
    }
    const children = document.createDocumentFragment();
    spec.items.forEach((item, index) => {
        const active = index === activeIndex;
        const group = document.createElement("div");
        group.className = `input-group vst-detail-repeating-group ${
            active ? "input-group-open" : "input-group-closed"
        } ${item.groupClassName ?? ""}`.trim();
        const header = document.createElement("span");
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
        symbol.textContent = active ? "⮟" : "⮞";
        const label = document.createElement("span");
        label.className = "header-label";
        label.textContent = item.label;
        const spacer = document.createElement("span");
        spacer.className = "header-label-spacer";
        const actions = document.createElement("span");
        actions.className = "vst-detail-repeating-group-actions";
        if (item.headerAction) {
            appendSectionHeaderAction(actions, item.headerAction);
        }
        const onDelete = item.onDelete ?? item.onShiftDelete;
        if (onDelete) {
            const remove = document.createElement("button");
            remove.type = "button";
            remove.className =
                `interrupt-button vst-btn-tiny vst-detail-delete vst-detail-repeating-group-delete ${spec.remove.className}`.trim();
            remove.textContent = "×";
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
        const editor = item.editor ?? (active ? spec.editor : undefined);
        if (editor) {
            appendSectionContent(content, editor, true);
        }
        content.hidden = !active;
        content.classList.toggle(
            "vst-detail-repeating-editor-active",
            active && editor !== undefined,
        );
        const activateOrToggle = (event: Event): void => {
            event.preventDefault();
            event.stopPropagation();
            if (!active && item.onSelect) {
                rememberedRepeaterItems.set(spec.key, index);
                item.onSelect();
                return;
            }
            const opening = content.hidden === true;
            if (opening) {
                for (const sibling of Array.from(
                    group.parentElement?.children ?? [],
                )) {
                    if (
                        sibling instanceof HTMLElement &&
                        sibling !== group &&
                        sibling.classList.contains("vst-detail-repeating-group")
                    ) {
                        setAccordionOpen(sibling, false);
                    }
                }
            }
            setAccordionOpen(group, opening);
            if (opening) {
                rememberedRepeaterItems.set(spec.key, index);
            } else if (rememberedRepeaterItems.get(spec.key) === index) {
                rememberedRepeaterItems.delete(spec.key);
            }
        };
        header.addEventListener("click", activateOrToggle);
        header.addEventListener("keydown", (event) => {
            if (event.key === "Enter" || event.key === " ") {
                activateOrToggle(event);
            }
        });
        group.append(header, content);
        children.appendChild(group);
        group.dataset.vstRepeaterItem = `${index}`;
    });
    const add = document.createElement("button");
    add.type = "button";
    add.className =
        `vst-add-btn vst-detail-repeating-add ${spec.add.className}`.trim();
    add.textContent = spec.add.label ?? "+ Add";
    add.title = spec.add.title;
    add.setAttribute("aria-label", spec.add.title);
    add.disabled = spec.add.disabled === true;
    add.addEventListener("click", (event) => {
        event.preventDefault();
        rememberedRepeaterItems.set(spec.key, spec.items.length);
        forceOpenRepeaterKeys.add(spec.key);
        spec.add.onClick();
    });
    children.appendChild(add);
    const built = buildAccordionSection({
        key: spec.key,
        label: spec.label,
        content: children,
        counter: spec.items.length,
        open: forceOpen || spec.open,
        className:
            `vst-detail-repeating-editor ${spec.sectionClass ?? ""}`.trim(),
    });
    built.section.dataset.vstRepeaterKey = spec.key;
    return {
        section: built.section,
        heading: built.heading,
        content: built.content,
        editor: spec.editor ?? null,
    };
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

export const wrapForm = (
    key: string,
    label: string,
    content: HTMLElement,
): HTMLElement => {
    const body = document.createElement("div");
    body.className = "vst-detail-body";
    body.appendChild(
        buildAccordionSection({
            key,
            label,
            content,
            open: true,
            flattenContent: true,
        }).section,
    );
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

export const buildStackSection = (
    key: string,
    label: string,
    colClass: string,
    open = false,
): { wrap: HTMLElement; col: HTMLElement } => {
    const col = document.createElement("div");
    col.className = `vst-detail-col ${colClass}`;
    const built = buildAccordionSection({
        key,
        label,
        content: col,
        open,
        flattenContent: true,
    });
    return { wrap: built.section, col: built.content };
};
