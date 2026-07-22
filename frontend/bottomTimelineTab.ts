const TAB_ID = "VideoStages-Timeline-Tab";
export const TIMELINE_BODY_ID = "videostages-timeline-body";

/**
 * Show/hide a green checkmark on the bottom-bar "Timeline" tab selector so the
 * VideoStages enable state is visible without opening the tab.
 */
export const updateTimelineTabIndicator = (enabled: boolean): void => {
    const navLink = document.querySelector<HTMLElement>(`a[href="#${TAB_ID}"]`);
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

const registerTabWithLayout = (navLink: HTMLElement): void => {
    if (typeof genTabLayout === "undefined" || !genTabLayout) {
        return;
    }
    const tab = new MovableGenTab(navLink, genTabLayout);
    genTabLayout.managedTabs.push(tab);
    if (genTabLayout.managedTabContainers.length > 0) {
        tab.contentElem.style.height = "100%";
        tab.contentElem.style.width = "100%";
        const parent = tab.contentElem.parentElement;
        if (parent && !genTabLayout.managedTabContainers.includes(parent)) {
            genTabLayout.managedTabContainers.push(parent);
        }
        tab.update();
        tab.navElem.addEventListener("click", () => {
            browserUtil.makeVisible(tab.contentElem);
        });
        genTabLayout.reapplyPositions();
    }
};

export const injectTimelineTab = (): HTMLElement | null => {
    const existing = document.getElementById(TIMELINE_BODY_ID);
    if (existing) {
        return existing;
    }
    const nav = document.getElementById("bottombartabcollection");
    const content = document.getElementById("t2i_bottom_bar_content");
    if (!nav || !content) {
        return null;
    }
    const li = document.createElement("li");
    li.className = "nav-item";
    li.setAttribute("role", "presentation");
    li.innerHTML = `<a class="nav-link translate" data-bs-toggle="tab" href="#${TAB_ID}" aria-selected="false" tabindex="-1" role="tab">VideoStages</a>`;
    const toolsNav = nav.querySelector('a[href="#Tools-Tab"]');
    if (toolsNav?.parentElement) {
        nav.insertBefore(li, toolsNav.parentElement);
    } else {
        nav.appendChild(li);
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
    content.appendChild(pane);
    const navLink = li.querySelector("a");
    if (navLink) {
        registerTabWithLayout(navLink);
    }
    return body;
};
