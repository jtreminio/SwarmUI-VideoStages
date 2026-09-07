import type { TimelineSelection } from "../types";

export function updateStickyItemLabels(pane: HTMLElement): void {
    const top = pane.getBoundingClientRect().top;
    for (const section of pane.querySelectorAll<HTMLElement>(
        ":scope > [data-section]",
    )) {
        const header = section.querySelector<HTMLElement>(
            ".vst-editor-section-heading",
        );
        const label = header?.querySelector<HTMLElement>(
            ".vst-editor-current-item",
        );
        if (!header || !label) continue;
        const bounds = header.getBoundingClientRect();
        let current = "";
        if (
            section.dataset.section === "stages" &&
            bounds.top <= top + 1 &&
            bounds.bottom > top
        ) {
            for (const item of section.querySelectorAll<HTMLElement>(
                ".vst-editor-item[data-item-id]",
            )) {
                const title = item.querySelector<HTMLElement>(
                    ".vst-editor-item-heading h4",
                );
                if (
                    title &&
                    title.getBoundingClientRect().bottom <= bounds.bottom &&
                    item.getBoundingClientRect().bottom > bounds.bottom
                ) {
                    current = `· ${item.dataset.itemLabel || title.textContent}`;
                    break;
                }
            }
        }
        if (label.textContent !== current) label.textContent = current;
        label.hidden = !current;
    }
}

export function sectionAtTop(pane: HTMLElement): string | null {
    const top = pane.getBoundingClientRect().top + 12;
    const panels = [
        ...pane.querySelectorAll<HTMLElement>(":scope > [data-section]"),
    ];
    return (
        (
            panels
                .filter((panel) => panel.getBoundingClientRect().top <= top)
                .at(-1) || panels[0]
        )?.dataset.section || null
    );
}

export function jumpToSection(pane: HTMLElement, key: string): void {
    const panel = [
        ...pane.querySelectorAll<HTMLElement>(":scope > [data-section]"),
    ].find((node) => node.dataset.section === key);
    if (panel)
        pane.scrollTop +=
            panel.getBoundingClientRect().top -
            pane.getBoundingClientRect().top;
}

export function markNavigation(nav: HTMLElement, key: string): void {
    for (const link of nav.querySelectorAll<HTMLElement>(
        "[data-section-link]",
    )) {
        const active = link.dataset.sectionLink === key;
        link.classList.toggle("active", active);
        if (active) link.setAttribute("aria-current", "location");
        else link.removeAttribute("aria-current");
    }
}

export function revealSelectedItem(
    pane: HTMLElement,
    selection: TimelineSelection,
): void {
    const item = Array.from(
        pane.querySelectorAll<HTMLElement>("[data-selection]"),
    ).find((node) => node.dataset.selection === JSON.stringify(selection));
    if (!item) return;
    const header = item
        .closest("[data-section]")
        ?.querySelector(".vst-editor-section-heading");
    const top =
        pane.getBoundingClientRect().top +
        (header?.getBoundingClientRect().height || 0);
    const bounds = item.getBoundingClientRect();
    if (bounds.top < top || bounds.bottom > pane.getBoundingClientRect().bottom)
        pane.scrollTop += bounds.top - top;
}

export function revealSelectedClip(root: HTMLElement): void {
    const sequence = root.querySelector<HTMLElement>(".vst-editor-sequence");
    const selected = sequence?.querySelector<HTMLElement>(
        ".vst-editor-clip.selected",
    );
    if (!sequence || !selected) return;
    const area = sequence.getBoundingClientRect();
    const bounds = selected.getBoundingClientRect();
    if (bounds.top < area.top) sequence.scrollTop += bounds.top - area.top;
    else if (bounds.bottom > area.bottom)
        sequence.scrollTop += bounds.bottom - area.bottom;
}
