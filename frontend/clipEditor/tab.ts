import {
    findHostBottomTabLink,
    getHostBottomTabMount,
    registerBottomTabWithHost,
} from "../host/swarmUiAdapters";
import { element } from "./components";

export const EDITOR_BODY_ID = "videostages-editor";
export const EDITOR_TAB_ID = "VideoStages-Timeline-Tab";

export const updateEditorTabIndicator = (enabled: boolean): void => {
    const link = findHostBottomTabLink(EDITOR_TAB_ID);
    if (!link) return;
    const mark = link.querySelector(".vst-tab-check");
    if (enabled && !mark) {
        const check = element("span", "vst-tab-check", "✓");
        check.setAttribute("aria-hidden", "true");
        link.append(check);
    } else if (!enabled) mark?.remove();
    link.title = enabled ? "Video Stages is enabled" : "";
};

const restorePreviewLink = (): void => {
    const hash = window.location.hash;
    if (!hash.split(",").includes("#VideoStages-Editor-Tab")) return;
    window.history.replaceState(
        null,
        "",
        hash.replace("VideoStages-Editor-Tab", EDITOR_TAB_ID),
    );
    const link = findHostBottomTabLink(EDITOR_TAB_ID);
    if (link) {
        link.click();
        // SwarmUI can restore .active before Bootstrap sees the click.
        link.setAttribute("aria-selected", "true");
        link.tabIndex = 0;
    }
};

export function mountClipEditor(): HTMLElement | null {
    const existing = document.getElementById(EDITOR_BODY_ID);
    if (existing) return existing;
    const host = getHostBottomTabMount();
    if (!host) return null;
    const item = element("li", "nav-item");
    item.setAttribute("role", "presentation");
    const link = element("a", "nav-link translate", "VideoStages");
    link.href = `#${EDITOR_TAB_ID}`;
    link.dataset.bsToggle = "tab";
    link.dataset.testid = "clip-editor-tab";
    link.setAttribute("role", "tab");
    link.setAttribute("aria-selected", "false");
    link.tabIndex = -1;
    item.append(link);
    host.nav.insertBefore(item, host.toolsTabItem);
    const pane = element("div", "tab-pane genpage-bottom-tab");
    pane.id = EDITOR_TAB_ID;
    pane.setAttribute("role", "tabpanel");
    const body = element("div", "vst-clip-editor");
    body.id = EDITOR_BODY_ID;
    body.dataset.testid = "clip-editor";
    pane.append(body);
    host.content.append(pane);
    registerBottomTabWithHost(link);
    window.removeEventListener("hashchange", restorePreviewLink);
    window.addEventListener("hashchange", restorePreviewLink);
    restorePreviewLink();
    return body;
}
