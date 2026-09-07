import type { DetailStripContext } from "../detailStrip/context";
import { buildToggleSwitch } from "../toggleSwitch";
import type { TimelineSelection } from "../types";

export interface EditorAction {
    key: string;
    label: string;
    kind?: "add" | "delete";
    disabled?: boolean;
    title?: string;
    configure?(button: HTMLButtonElement): void;
    run(): void;
}

export interface EditorItem {
    key: string;
    label: string;
    contextLabel?: string;
    selection?: TimelineSelection;
    fields(): HTMLElement | HTMLElement[];
    actions?: EditorAction[];
    toggle?: EditorAction & { checked: boolean };
}

export interface EditorSection {
    key: string;
    label: string;
    items: EditorItem[];
    layout?: "cards" | "rows";
    actions?: EditorAction[];
}

export function element<K extends keyof HTMLElementTagNameMap>(
    tag: K,
    className: string,
    text?: string,
): HTMLElementTagNameMap[K] {
    const node = document.createElement(tag);
    node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
}

export function actionButton(action: EditorAction): HTMLButtonElement {
    const button = element("button", "basic-button small-button", action.label);
    button.type = "button";
    button.disabled = action.disabled ?? false;
    if (action.title) button.title = action.title;
    action.configure?.(button);
    button.dataset.testid = action.key;
    if (action.kind) button.dataset.action = action.kind;
    if (action.kind === "delete") {
        const icon = document.createElementNS(
            "http://www.w3.org/2000/svg",
            "svg",
        );
        icon.setAttribute("viewBox", "0 0 24 24");
        icon.setAttribute("aria-hidden", "true");
        icon.setAttribute("focusable", "false");
        icon.innerHTML =
            '<path d="M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7M14 10v7" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/>';
        button.prepend(icon);
    }
    button.addEventListener("click", action.run);
    return button;
}

// Repeated production panels sometimes use the same local draft/focus key.
export function scopeFields(
    context: DetailStripContext,
    scope: string,
): DetailStripContext {
    return {
        ...context,
        debouncedCommit: (key, mutate) =>
            context.debouncedCommit(`${scope}/${key}`, mutate),
        debouncedCommitState: (key, mutate) =>
            context.debouncedCommitState(`${scope}/${key}`, mutate),
        buildClampedNumber: (options) =>
            context.buildClampedNumber({
                ...options,
                key: `${scope}/${options.key}`,
            }),
    };
}

export function tagFields(root: HTMLElement, scope: string): void {
    const keys = new Map<string, number>();
    for (const control of root.querySelectorAll<HTMLElement>(
        "input, select, textarea, button",
    )) {
        const focus = control.dataset.vstFocusKey;
        if (focus && !focus.startsWith(`${scope}/`))
            control.dataset.vstFocusKey = `${scope}/${focus}`;
        const field = control.closest<HTMLElement>(
            ".vst-detail-field, .auto-input",
        );
        const label = (
            field?.querySelector(".vst-detail-field-label, .auto-input-name") ??
            field?.querySelector("label")
        )?.childNodes[0]?.textContent?.trim();
        const name =
            focus?.split("/").at(-1) ||
            label ||
            control.getAttribute("aria-label") ||
            control.textContent ||
            control.tagName;
        const base = name
            .trim()
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, "-")
            .replace(/^-|-$/g, "");
        const kind =
            control instanceof HTMLInputElement
                ? control.type
                : control.tagName.toLowerCase();
        const key = `field-${base}-${kind}`;
        const duplicate = keys.get(key) || 0;
        keys.set(key, duplicate + 1);
        control.dataset.testid = `${key}${duplicate ? `-${duplicate}` : ""}`;
        control.dataset.field = base;
    }
}

export function sectionPanel(
    section: EditorSection,
    clipId: string,
): HTMLElement {
    const panel = element(
        "section",
        "input-group input-group-open vst-editor-section",
    );
    panel.dataset.section = section.key;
    panel.dataset.testid = `section-${section.key}`;
    panel.dataset.clipId = clipId;
    const header = element(
        "header",
        "input-group-header vst-editor-section-heading",
    );
    const title = element("h3", "", section.label);
    const currentItem = element("span", "vst-editor-current-item");
    currentItem.dataset.testid = "sticky-item-label";
    currentItem.hidden = true;
    title.id = `vst-editor-heading-${section.key}`;
    panel.setAttribute("aria-labelledby", title.id);
    const actions = element("div", "vst-editor-actions");
    actions.append(...(section.actions || []).map(actionButton));
    // Core delegates header clicks to its collapsible parameter groups.
    header.addEventListener("click", (event) => event.stopPropagation());
    header.append(title, actions, currentItem);
    panel.append(header);
    const rows = section.layout === "rows";
    const items = rows
        ? element("div", "input-group-content vst-editor-card vst-editor-rows")
        : panel;
    if (rows && section.items.length) panel.append(items);
    for (const item of section.items) {
        const body = element(
            "article",
            rows
                ? "vst-editor-item vst-editor-row"
                : "input-group-content vst-editor-card vst-editor-item",
        );
        body.dataset.itemId = item.key;
        body.dataset.itemLabel = item.contextLabel ?? item.label;
        body.dataset.testid = "editor-item";
        if (item.selection)
            body.dataset.selection = JSON.stringify(item.selection);
        const itemHeading = element("div", "vst-editor-item-heading");
        const itemTitle = element(
            "h4",
            "",
            rows ? item.contextLabel : item.label,
        );
        if (rows) itemTitle.setAttribute("aria-label", item.label);
        itemHeading.append(itemTitle);
        if (item.toggle) {
            const action = item.toggle;
            body.dataset.enabled = String(action.checked);
            const state = element("div", "vst-editor-item-state");
            const toggle = buildToggleSwitch({
                checked: action.checked,
                title: action.label,
                onChange: action.run,
            });
            const input = toggle.querySelector("input");
            const label = element(
                "label",
                "",
                action.checked ? "Enabled" : "Skipped",
            );
            if (input) {
                input.dataset.testid = action.key;
                input.setAttribute("role", "switch");
                input.setAttribute("aria-checked", String(action.checked));
                label.htmlFor = input.id;
            }
            state.append(toggle, label);
            itemHeading.append(state);
        }
        const controls = element("div", "vst-editor-actions");
        controls.append(...(item.actions || []).map(actionButton));
        if (!rows) itemHeading.append(controls);
        if (
            section.items.length > 1 ||
            item.actions?.length ||
            item.label !== section.label
        )
            body.append(itemHeading);
        const panels = item.fields();
        const fields = Array.isArray(panels)
            ? element("div", "vst-detail-fields")
            : panels;
        // Compose field-only panels without carrying their sidebar containers.
        if (Array.isArray(panels)) {
            for (const panel of panels) fields.append(...panel.childNodes);
        }
        fields.classList.remove("input-group-content");
        fields.classList.add("vst-editor-fields");
        tagFields(fields, item.key);
        body.append(fields);
        if (rows) body.append(controls);
        items.append(body);
    }
    if (!section.items.length)
        panel.append(
            element(
                "p",
                "vst-editor-empty",
                `No ${section.label.toLowerCase()} added.`,
            ),
        );
    return panel;
}
