import { clamp } from "./constants";
import { getVideoStagesHostBridge } from "./host";
import {
    enhanceHostPromptEditor,
    hasHostInputBrowser,
    openHostInputBrowser,
    renderHostSlider,
    showHostPopover,
} from "./host/swarmUiAdapters";
import { getTimelineAuthoringSettings } from "./timelineAuthoringSettings";

let sliderSeq = 0;
let helpSeq = 0;
let checkboxSeq = 0;
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
        isPot: opts?.isPot,
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
 * Host server picks arrive in `dataset.filedata` and require the hidden
 * preview and filename nodes. Both server and browser picks become data URIs.
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
    target: HTMLElement,
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
    const actions = document.createElement("span");
    actions.className = "vst-detail-repeating-group-actions";
    for (const action of headerActions) {
        appendSectionHeaderAction(actions, action);
    }
    target.appendChild(actions);
};

export interface StaticSectionSpec {
    key: string;
    label: string;
    content: HTMLElement | DocumentFragment;
    className?: string;
    flattenContent?: boolean;
    headerAction?: SectionHeaderAction;
    headerActions?: SectionHeaderAction[];
}

const rememberedAccordionSections = new Set<string>();
const seenAccordionSections = new Set<string>();

export const resetRememberedAccordionSections = (): void => {
    rememberedAccordionSections.clear();
    seenAccordionSections.clear();
    rememberedRepeaterItems.clear();
    rememberedRepeaterOpenItems.clear();
    forceOpenRepeaterKeys.clear();
    persistedRepeaterState = null;
};

/** Native SwarmUI group without disclosure behavior. */
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
    appendSectionHeaderActions(labelWrap, spec);
    header.appendChild(labelWrap);

    const content = document.createElement("div");
    content.className = "input-group-content vst-detail-section-content";
    appendSectionContent(content, spec.content, spec.flattenContent === true);
    section.append(header, content);
    return { section, heading, content };
};

/** Native SwarmUI disclosure group with rebuild-stable open state. */
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
    section.dataset.vstAccordionKey = spec.key;

    const header = document.createElement("span");
    header.className =
        "input-group-header input-group-shrinkable vst-detail-section-header";
    header.tabIndex = 0;
    header.setAttribute("role", "button");
    header.setAttribute("aria-expanded", `${open}`);

    const labelWrap = document.createElement("span");
    labelWrap.className = "header-label-wrap";
    const symbol = document.createElement("span");
    symbol.className = "auto-symbol";
    symbol.textContent = open ? "⮟" : "⮞";
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
    appendSectionHeaderActions(labelWrap, spec);
    header.appendChild(labelWrap);

    const content = document.createElement("div");
    content.className = "input-group-content vst-detail-section-content";
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
        if (event.key === "Enter" || event.key === " ") {
            toggle(event);
        }
    });
    section.append(header, content);
    return { section, heading, content };
};

export interface RepeatingGroupItem {
    label: string;
    stateKey?: string;
    focusKey?: string;
    title?: string;
    active?: boolean;
    className?: string;
    groupClassName?: string;
    editor?: HTMLElement;
    onSelect?: () => void;
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
    editorForItem?: (index: number) => HTMLElement | undefined;
    sectionClass?: string;
    open?: boolean;
    defaultActiveIndex?: number | null;
}

const rememberedRepeaterItems = new Map<string, number>();
const rememberedRepeaterOpenItems = new Map<string, Set<number>>();
const forceOpenRepeaterKeys = new Set<string>();
const REPEATER_OPEN_STORAGE_KEY = "videostages.detail.openRepeaterItems";
interface PersistedRepeaterState {
    open: Set<string>;
    known: Set<string>;
}
let persistedRepeaterState: PersistedRepeaterState | null = null;

const storedRepeaterState = (): PersistedRepeaterState => {
    if (persistedRepeaterState) {
        return persistedRepeaterState;
    }
    try {
        const parsed: unknown = JSON.parse(
            localStorage.getItem(REPEATER_OPEN_STORAGE_KEY) ?? "[]",
        );
        const strings = (value: unknown): string[] =>
            Array.isArray(value)
                ? value.filter(
                      (entry): entry is string => typeof entry === "string",
                  )
                : [];
        const open = strings(
            Array.isArray(parsed)
                ? parsed
                : typeof parsed === "object" && parsed !== null
                  ? Reflect.get(parsed, "open")
                  : [],
        );
        const known = Array.isArray(parsed)
            ? open
            : strings(
                  typeof parsed === "object" && parsed !== null
                      ? Reflect.get(parsed, "known")
                      : [],
              );
        persistedRepeaterState = {
            open: new Set(open),
            known: new Set(known),
        };
    } catch {
        persistedRepeaterState = { open: new Set(), known: new Set() };
    }
    return persistedRepeaterState;
};

const writeRepeaterState = (stored: PersistedRepeaterState): void => {
    try {
        localStorage.setItem(
            REPEATER_OPEN_STORAGE_KEY,
            JSON.stringify({
                open: [...stored.open].sort(),
                known: [...stored.known].sort(),
            }),
        );
    } catch {}
};

const persistRepeaterOpenItems = (
    items: readonly RepeatingGroupItem[],
    openItems: ReadonlySet<number>,
): void => {
    const stored = storedRepeaterState();
    let changed = false;
    items.forEach((item, index) => {
        if (!item.stateKey) {
            return;
        }
        if (!stored.known.has(item.stateKey)) {
            stored.known.add(item.stateKey);
            changed = true;
        }
        const open = openItems.has(index);
        if (open !== stored.open.has(item.stateKey)) {
            changed = true;
            if (open) {
                stored.open.add(item.stateKey);
            } else {
                stored.open.delete(item.stateKey);
            }
        }
    });
    if (!changed) {
        return;
    }
    writeRepeaterState(stored);
};

const forgetRepeaterItem = (item: RepeatingGroupItem): void => {
    if (!item.stateKey) {
        return;
    }
    const stored = storedRepeaterState();
    const removedOpen = stored.open.delete(item.stateKey);
    const removedKnown = stored.known.delete(item.stateKey);
    if (!removedOpen && !removedKnown) {
        return;
    }
    writeRepeaterState(stored);
};

const runRepeaterStructuralAction = (
    source: HTMLElement,
    action: () => void,
): void => {
    const previousBody = source.closest<HTMLElement>(".vst-detail-body");
    const detail = previousBody?.closest<HTMLElement>(".vst-detail");
    const scrollTop = previousBody?.scrollTop;
    const repeaterKey = source.closest<HTMLElement>("[data-vst-repeater-key]")
        ?.dataset.vstRepeaterKey;
    const anchorTop = source.classList.contains("vst-detail-repeating-add")
        ? source.getBoundingClientRect().top
        : null;
    action();
    if (scrollTop === undefined) {
        return;
    }
    const currentBody = (): HTMLElement | null =>
        detail?.querySelector<HTMLElement>(".vst-detail-body") ??
        (previousBody?.isConnected ? previousBody : null);
    const restore = (): void => {
        const body = currentBody();
        if (!body) {
            return;
        }
        if (anchorTop !== null && repeaterKey) {
            const section = Array.from(
                body.querySelectorAll<HTMLElement>("[data-vst-repeater-key]"),
            ).find(
                (candidate) => candidate.dataset.vstRepeaterKey === repeaterKey,
            );
            const nextAdd = section?.querySelector<HTMLElement>(
                ":scope > .input-group-content > .vst-detail-repeating-add",
            );
            if (nextAdd) {
                const nextTop = nextAdd.getBoundingClientRect().top;
                body.scrollTop =
                    anchorTop === 0 && nextTop === 0
                        ? scrollTop
                        : body.scrollTop + nextTop - anchorTop;
                return;
            }
        }
        body.scrollTop = scrollTop;
    };
    if (anchorTop !== null && typeof requestAnimationFrame === "function") {
        requestAnimationFrame(restore);
        return;
    }
    restore();
    if (typeof requestAnimationFrame === "function") {
        requestAnimationFrame(() => {
            const body = currentBody();
            if (body) {
                body.scrollTop = scrollTop;
            }
        });
    }
};

/** Shared outer group, item disclosures, Add action, and Delete actions. */
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
    const isValidIndex = (index: number | null | undefined): index is number =>
        index !== null &&
        index !== undefined &&
        index >= 0 &&
        index < spec.items.length;
    const rememberedIndex = rememberedRepeaterItems.get(spec.key);
    const validRememberedIndex = isValidIndex(rememberedIndex)
        ? rememberedIndex
        : null;
    const selectionChanged =
        rememberedIndex !== undefined &&
        explicitActiveIndex >= 0 &&
        explicitActiveIndex !== validRememberedIndex;
    if (rememberedIndex !== undefined && validRememberedIndex === null) {
        rememberedRepeaterItems.delete(spec.key);
        forceOpenRepeaterKeys.delete(spec.key);
    }
    const forceOpen =
        forceOpenRepeaterKeys.has(spec.key) && validRememberedIndex !== null;
    const defaultActiveIndex = isValidIndex(spec.defaultActiveIndex)
        ? spec.defaultActiveIndex
        : null;
    const activeIndex = forceOpen
        ? validRememberedIndex
        : explicitActiveIndex >= 0
          ? explicitActiveIndex
          : (validRememberedIndex ?? defaultActiveIndex);
    if (explicitActiveIndex >= 0 && !forceOpen) {
        rememberedRepeaterItems.set(spec.key, explicitActiveIndex);
    } else if (activeIndex !== null) {
        rememberedRepeaterItems.set(spec.key, activeIndex);
    }
    if (forceOpen) {
        forceOpenRepeaterKeys.delete(spec.key);
    }
    const autoCollapse = getTimelineAuthoringSettings().autoCollapse;
    const rememberedOpenItems = rememberedRepeaterOpenItems.get(spec.key);
    const storedState = storedRepeaterState();
    const hasStoredItemState = spec.items.some(
        (item) => item.stateKey && storedState.known.has(item.stateKey),
    );
    const newlyPopulated =
        spec.items.length > 0 &&
        rememberedOpenItems !== undefined &&
        rememberedIndex === undefined;
    const openItems = new Set(
        rememberedOpenItems ??
            spec.items.flatMap((item, index) =>
                item.stateKey && storedState.open.has(item.stateKey)
                    ? [index]
                    : [],
            ),
    );
    for (const index of openItems) {
        if (index < 0 || index >= spec.items.length) {
            openItems.delete(index);
        }
    }
    if (
        activeIndex !== null &&
        (forceOpen ||
            selectionChanged ||
            newlyPopulated ||
            (rememberedOpenItems === undefined && !hasStoredItemState))
    ) {
        if (autoCollapse) {
            openItems.clear();
        }
        openItems.add(activeIndex);
    }
    rememberedRepeaterOpenItems.set(spec.key, openItems);
    persistRepeaterOpenItems(spec.items, openItems);
    const children = document.createDocumentFragment();
    spec.items.forEach((item, index) => {
        const active = index === activeIndex;
        const open = openItems.has(index);
        const group = document.createElement("div");
        group.className = `input-group vst-detail-repeating-group ${
            open ? "input-group-open" : "input-group-closed"
        } ${item.groupClassName ?? ""}`.trim();
        const header = document.createElement("span");
        header.className =
            `input-group-header input-group-shrinkable vst-detail-repeating-group-header ${item.className ?? ""}`.trim();
        header.tabIndex = 0;
        header.setAttribute("role", "button");
        header.setAttribute("aria-expanded", `${open}`);
        header.setAttribute("aria-pressed", `${active}`);
        if (item.focusKey) {
            header.dataset.vstFocusKey = item.focusKey;
        }
        if (item.title) {
            header.title = item.title;
        }
        const labelWrap = document.createElement("span");
        labelWrap.className = "header-label-wrap";
        const symbol = document.createElement("span");
        symbol.className = "auto-symbol";
        symbol.textContent = open ? "⮟" : "⮞";
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
        const onDelete = item.onDelete;
        if (onDelete) {
            const remove = buildDetailActionButton({
                label: "×",
                title:
                    item.deleteTitle ??
                    (active ? spec.remove.title : `Delete ${item.label}`),
                className:
                    `vst-btn-tiny vst-detail-delete vst-detail-repeating-group-delete ${spec.remove.className}`.trim(),
                variant: "interrupt",
                disabled: item.deleteDisabled,
                stopPropagation: true,
                onClick: (button) => {
                    forgetRepeaterItem(item);
                    runRepeaterStructuralAction(button, onDelete);
                },
            });
            actions.appendChild(remove);
        }
        labelWrap.append(symbol, label, spacer, actions);
        header.appendChild(labelWrap);
        const content = document.createElement("div");
        content.className =
            "input-group-content vst-detail-repeating-group-content";
        let editor = open
            ? (item.editor ??
              spec.editorForItem?.(index) ??
              (active ? spec.editor : undefined))
            : undefined;
        if (editor) {
            appendSectionContent(content, editor, true);
        }
        content.hidden = !open;
        content.classList.toggle(
            "vst-detail-repeating-editor-active",
            open && editor !== undefined,
        );
        const activateOrToggle = (event: Event): void => {
            event.preventDefault();
            event.stopPropagation();
            if (!active && item.onSelect) {
                if (getTimelineAuthoringSettings().autoCollapse) {
                    rememberedRepeaterOpenItems.set(spec.key, new Set([index]));
                } else {
                    const remembered =
                        rememberedRepeaterOpenItems.get(spec.key) ??
                        new Set<number>();
                    for (const sibling of Array.from(
                        group.parentElement?.children ?? [],
                    )) {
                        if (
                            sibling instanceof HTMLElement &&
                            sibling.classList.contains(
                                "vst-detail-repeating-group",
                            ) &&
                            sibling.classList.contains("input-group-open")
                        ) {
                            const siblingIndex = Number(
                                sibling.dataset.vstRepeaterItem,
                            );
                            if (Number.isInteger(siblingIndex)) {
                                remembered.add(siblingIndex);
                            }
                        }
                    }
                    remembered.add(index);
                    rememberedRepeaterOpenItems.set(spec.key, remembered);
                }
                persistRepeaterOpenItems(
                    spec.items,
                    rememberedRepeaterOpenItems.get(spec.key) ?? new Set(),
                );
                rememberedRepeaterItems.set(spec.key, index);
                item.onSelect();
                return;
            }
            const opening = content.hidden === true;
            const collapseItems = getTimelineAuthoringSettings().autoCollapse;
            if (opening && collapseItems) {
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
            if (opening && !editor) {
                editor =
                    item.editor ??
                    spec.editorForItem?.(index) ??
                    (active ? spec.editor : undefined);
                if (editor) {
                    appendSectionContent(content, editor, true);
                    getVideoStagesHostBridge().enableSliders(content);
                }
            }
            setAccordionOpen(group, opening);
            if (opening) {
                rememberedRepeaterItems.set(spec.key, index);
                if (collapseItems) {
                    rememberedRepeaterOpenItems.set(spec.key, new Set([index]));
                } else {
                    const remembered =
                        rememberedRepeaterOpenItems.get(spec.key) ??
                        new Set<number>();
                    remembered.add(index);
                    rememberedRepeaterOpenItems.set(spec.key, remembered);
                }
            } else {
                rememberedRepeaterOpenItems.get(spec.key)?.delete(index);
            }
            persistRepeaterOpenItems(
                spec.items,
                rememberedRepeaterOpenItems.get(spec.key) ?? new Set(),
            );
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
    const add = buildDetailActionButton({
        label: spec.add.label ?? "+ Add",
        title: spec.add.title,
        className:
            `small-button vst-detail-repeating-add ${spec.add.className}`.trim(),
        disabled: spec.add.disabled,
        onClick: (button) => {
            const nextIndex = spec.items.length;
            if (getTimelineAuthoringSettings().autoCollapse) {
                rememberedRepeaterOpenItems.set(spec.key, new Set([nextIndex]));
            } else {
                const remembered =
                    rememberedRepeaterOpenItems.get(spec.key) ??
                    new Set<number>();
                for (const sibling of Array.from(
                    button.parentElement?.children ?? [],
                )) {
                    if (
                        sibling instanceof HTMLElement &&
                        sibling.classList.contains(
                            "vst-detail-repeating-group",
                        ) &&
                        sibling.classList.contains("input-group-open")
                    ) {
                        const siblingIndex = Number(
                            sibling.dataset.vstRepeaterItem,
                        );
                        if (Number.isInteger(siblingIndex)) {
                            remembered.add(siblingIndex);
                        }
                    }
                }
                remembered.add(nextIndex);
                rememberedRepeaterOpenItems.set(spec.key, remembered);
            }
            rememberedRepeaterItems.set(spec.key, spec.items.length);
            forceOpenRepeaterKeys.add(spec.key);
            runRepeaterStructuralAction(button, spec.add.onClick);
        },
    });
    children.appendChild(add);
    const built = buildAccordionSection({
        key: spec.key,
        label: spec.label,
        content: children,
        counter: spec.items.length,
        // Empty repeaters stay open so their Add action remains reachable.
        open: forceOpen || spec.items.length === 0 || spec.open,
        defaultOpen: spec.items.length > 0,
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

/** Tag a control for focus restoration after a rebuild. */
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
