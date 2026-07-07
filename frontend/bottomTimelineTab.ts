const TAB_ID = "VideoStages-Timeline-Tab";
export const TIMELINE_BODY_ID = "videostages-timeline-body";

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
    li.innerHTML = `<a class="nav-link translate" data-bs-toggle="tab" href="#${TAB_ID}" aria-selected="false" tabindex="-1" role="tab">Timeline</a>`;
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
    const body = document.createElement("div");
    body.className = "vst-timeline";
    body.id = TIMELINE_BODY_ID;
    pane.appendChild(body);
    content.appendChild(pane);
    const navLink = li.querySelector("a");
    if (navLink) {
        registerTabWithLayout(navLink);
    }
    return body;
};
