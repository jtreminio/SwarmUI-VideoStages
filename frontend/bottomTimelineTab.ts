import {
    findHostBottomTabLink,
    getHostBottomTabMount,
    registerBottomTabWithHost,
} from "./host/swarmUiAdapters";

const TAB_ID = "VideoStages-Timeline-Tab";
export const TIMELINE_BODY_ID = "videostages-timeline-body";

/**
 * Show/hide a green checkmark on the bottom-bar "Timeline" tab selector so the
 * VideoStages enable state is visible without opening the tab.
 */
export const updateTimelineTabIndicator = (enabled: boolean): void => {
    const navLink = findHostBottomTabLink(TAB_ID);
    if (!navLink) {
        return;
    }
    const mark = navLink.querySelector(".vst-tab-check");
    if (enabled && !mark) {
        const check = document.createElement("span");
        check.className = "vst-tab-check";
        check.setAttribute("aria-hidden", "true");
        check.textContent = "✓";
        navLink.appendChild(check);
        navLink.title = "Video Stages is enabled";
    } else if (!enabled && mark) {
        mark.remove();
        navLink.removeAttribute("title");
    }
};

export const injectTimelineTab = (): HTMLElement | null => {
    const existing = document.getElementById(TIMELINE_BODY_ID);
    if (existing) {
        return existing;
    }
    const mount = getHostBottomTabMount();
    if (!mount) {
        return null;
    }
    const li = document.createElement("li");
    li.className = "nav-item";
    li.setAttribute("role", "presentation");
    li.innerHTML = `<a class="nav-link translate" data-bs-toggle="tab" href="#${TAB_ID}" aria-selected="false" tabindex="-1" role="tab">VideoStages</a>`;
    if (mount.toolsTabItem) {
        mount.nav.insertBefore(li, mount.toolsTabItem);
    } else {
        mount.nav.appendChild(li);
    }
    const pane = document.createElement("div");
    pane.className = "tab-pane genpage-bottom-tab";
    pane.id = TAB_ID;
    pane.setAttribute("role", "tabpanel");
    // Outer flex-row shell: the left dock (`.vst-detail`, created by the detail
    // strip owner) and the tracks column (`.vst-right`, wiped by renderTimeline)
    // are siblings, so the tracks re-render can never destroy the dock.
    const shell = document.createElement("div");
    shell.className = "vst-timeline";
    const body = document.createElement("div");
    body.className = "vst-right";
    body.id = TIMELINE_BODY_ID;
    shell.appendChild(body);
    pane.appendChild(shell);
    mount.content.appendChild(pane);
    const navLink = li.querySelector("a");
    if (navLink) {
        registerBottomTabWithHost(navLink);
    }
    return body;
};
