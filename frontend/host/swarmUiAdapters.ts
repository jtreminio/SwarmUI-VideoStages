/**
 * Narrow adapters for legacy SwarmUI UI services that are not part of the
 * VideoStages document/carrier bridge. Keeping these reads here makes the host
 * boundary explicit without coupling application modules to Swarm globals.
 */

export interface HostBottomTabMount {
    nav: HTMLElement;
    content: HTMLElement;
    toolsTabItem: Element | null;
}

export const findHostBottomTabLink = (
    tabId: string,
): HTMLAnchorElement | null =>
    document.querySelector<HTMLAnchorElement>(`a[href="#${tabId}"]`);

export const getHostBottomTabMount = (): HostBottomTabMount | null => {
    const nav = document.getElementById("bottombartabcollection");
    const content = document.getElementById("t2i_bottom_bar_content");
    if (!nav || !content) {
        return null;
    }
    return {
        nav,
        content,
        toolsTabItem:
            nav.querySelector('a[href="#Tools-Tab"]')?.parentElement ?? null,
    };
};

export const registerBottomTabWithHost = (navLink: HTMLElement): void => {
    if (typeof genTabLayout === "undefined" || !genTabLayout) {
        return;
    }
    const tab = new MovableGenTab(navLink, genTabLayout);
    genTabLayout.managedTabs.push(tab);
    if (genTabLayout.managedTabContainers.length === 0) {
        return;
    }
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
};

export const showHostPopover = (id: string, event?: Event): void => {
    if (typeof doPopover === "function") {
        doPopover(id, event);
    }
};

export interface HostSliderSpec {
    id: string;
    label: string;
    value: number;
    min: number;
    max: number;
    viewMin?: number;
    viewMax?: number;
    step: number;
}

export const renderHostSlider = (spec: HostSliderSpec): string =>
    makeSliderInput(
        null,
        spec.id,
        "",
        spec.label,
        "",
        spec.value,
        spec.min,
        spec.max,
        spec.viewMin ?? spec.min,
        spec.viewMax ?? spec.max,
        spec.step,
        false,
        false,
        false,
    );

export const enhanceHostPromptEditor = (editor: HTMLElement): void => {
    if (typeof textPromptAddKeydownHandler === "function") {
        textPromptAddKeydownHandler(editor);
    }
};

export const hasHostInputBrowser = (): boolean =>
    typeof inputBrowserHelper !== "undefined" && Boolean(inputBrowserHelper);

export const openHostInputBrowser = (
    inputId: string,
    browserTypes: string[],
): void => {
    if (typeof inputBrowserHelper !== "undefined" && inputBrowserHelper) {
        inputBrowserHelper.openInputBrowser(inputId, browserTypes);
    }
};

export const refreshHostParameters = (): void => {
    if (typeof refreshParameterValues === "function") {
        refreshParameterValues(true);
    }
};

export const canRequestHostWs = (): boolean =>
    typeof makeWSRequest === "function";

export const requestHostWs = (
    route: string,
    payload: Record<string, unknown>,
    onMessage: (data: Record<string, unknown>) => void,
    onError: (error: unknown) => void,
): void => {
    if (typeof makeWSRequest !== "function") {
        return;
    }
    makeWSRequest(route, payload, onMessage, 0, onError);
};
