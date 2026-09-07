import type { AuthoringDocument } from "../types";
import {
    actionButton,
    type EditorSection,
    element,
    sectionPanel,
} from "./components";
import { flowMap } from "./flowMap";

export interface EditorNavigation {
    selectClip(index: number, section?: string, focusPrompt?: boolean): void;
    deleteClip(index: number): void;
    moveClip(from: number, to: number): void;
    jump(section: string): void;
    addClip(): void;
    setEnabled(enabled: boolean): void;
    toggleMap(): void;
}

export function renderEditor(
    root: HTMLElement,
    state: AuthoringDocument,
    selected: number,
    sections: EditorSection[],
    navigation: EditorNavigation,
    collapsed: boolean,
): void {
    const strip = flowMap(state, selected, navigation, collapsed);
    root.classList.toggle("vst-editor-map-collapsed", collapsed);
    const workspace = element("div", "vst-editor-workspace");
    const rail = element("nav", "vst-editor-navigation");
    rail.setAttribute("aria-label", "Clip sections");
    rail.dataset.testid = "section-navigation";
    const pane = element("main", "vst-editor-scroll");
    pane.dataset.testid = "clip-sections";
    pane.setAttribute("aria-label", "Clip settings");
    for (const section of sections) {
        const link = actionButton({
            key: `nav-${section.key}`,
            label: section.label,
            run: () => navigation.jump(section.key),
        });
        link.dataset.sectionLink = section.key;
        if (
            [
                "stages",
                "keyframes",
                "references",
                "loras",
                "ic-loras",
                "relay-prompts",
            ].includes(section.key)
        )
            link.append(element("span", "", String(section.items.length)));
        rail.append(link);
        pane.append(sectionPanel(section, state.clips[selected]?.id || ""));
    }
    if (!state.clips.length) {
        const empty = element("div", "vst-editor-start");
        empty.append(
            element("p", "", "Add a clip to start your video."),
            actionButton({
                key: "add-first-clip",
                label: "+ Add clip",
                run: navigation.addClip,
            }),
        );
        pane.append(empty);
    }
    workspace.append(rail, pane);
    root.replaceChildren(strip, workspace);
}
