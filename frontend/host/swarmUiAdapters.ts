import { isRecord } from "../utils";

export const restoreHostMediaPreview = (
    input: HTMLInputElement,
    src: string,
    name: string,
): void => {
    if (typeof setMediaFileDirect !== "function") return;
    setMediaFileDirect(input, src, getMediaType(src), name);
    const player = input.closest(".auto-input")?.querySelector("video, audio");
    if (player instanceof HTMLMediaElement) {
        player.classList.add("vst-sidebar-media-preview");
        player.autoplay = false;
        player.controls = true;
        player.preload = "metadata";
    }
};

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

export const showHostActionMenu = (
    anchor: HTMLElement,
    actions: { key: string; action(): void }[],
): void => {
    if (typeof AdvancedPopover !== "function") return;
    const rect = anchor.getBoundingClientRect();
    new AdvancedPopover(
        "videostages-actions",
        actions,
        false,
        rect.left,
        rect.bottom,
        document.body,
        null,
        rect.height,
    );
};

export const installHostFeature = (
    featureId: string,
    buttonContainerId: string,
): boolean => {
    if (typeof installFeatureById !== "function") {
        return false;
    }
    installFeatureById(featureId, buttonContainerId);
    return true;
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
    isPot?: boolean;
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
        spec.isPot ?? false,
        false,
        false,
    );

export const enhanceHostPromptEditor = (editor: HTMLElement): void => {
    if (typeof textPromptAddKeydownHandler === "function") {
        textPromptAddKeydownHandler(editor);
    }
};

type GenerationInputProjector = (input: Record<string, unknown>) => void;
type HostGenerationInputCollector = (
    inputOverrides?: Record<string, unknown>,
    inputPreOverrides?: Record<string, unknown>,
) => Record<string, unknown>;

let generationInputProjector: GenerationInputProjector | null = null;
let installedGenerationWrapper: ((...args: unknown[]) => void) | null = null;
let installedInputCollector: HostGenerationInputCollector | null = null;
let generationProjectorRetry: ReturnType<typeof setTimeout> | null = null;

const projectGenerationInput = (
    actualInput: unknown,
    beforeProjection?: () => void,
): void => {
    try {
        beforeProjection?.();
        if (isRecord(actualInput)) {
            generationInputProjector?.(actualInput);
        }
    } catch (error) {
        if (typeof showError === "function") {
            showError(`${error}`);
        }
        throw error;
    }
};

const installHostInputCollectorProjector = (): void => {
    const host = globalThis as typeof globalThis & {
        getGenInput?: HostGenerationInputCollector;
    };
    if (
        typeof host.getGenInput !== "function" ||
        host.getGenInput === installedInputCollector
    ) {
        return;
    }
    const original = host.getGenInput;
    const wrapper: HostGenerationInputCollector = function (
        this: unknown,
        ...args: Parameters<HostGenerationInputCollector>
    ): Record<string, unknown> {
        const actualInput = original.apply(this, args);
        projectGenerationInput(actualInput);
        return actualInput;
    };
    host.getGenInput = wrapper;
    installedInputCollector = wrapper;
};

const retryGenerationProjector = (): void => {
    if (generationProjectorRetry !== null) {
        return;
    }
    generationProjectorRetry = setTimeout(() => {
        generationProjectorRetry = null;
        if (generationInputProjector) {
            installHostGenerationInputProjector(generationInputProjector);
        }
    }, 250);
};

export const installHostGenerationInputProjector = (
    projector: GenerationInputProjector,
): boolean => {
    generationInputProjector = projector;
    installHostInputCollectorProjector();
    if (
        typeof mainGenHandler === "undefined" ||
        typeof mainGenHandler?.doGenerate !== "function"
    ) {
        retryGenerationProjector();
        return false;
    }
    if (generationProjectorRetry !== null) {
        clearTimeout(generationProjectorRetry);
        generationProjectorRetry = null;
    }
    if (mainGenHandler.doGenerate === installedGenerationWrapper) {
        return true;
    }
    const original = mainGenHandler.doGenerate;
    const wrapper = function (this: unknown, ...args: unknown[]): void {
        const existingPostCollect = args[2];
        args[2] = (actualInput: unknown): void =>
            projectGenerationInput(
                actualInput,
                typeof existingPostCollect === "function"
                    ? () => existingPostCollect(actualInput)
                    : undefined,
            );
        original.apply(this, args);
    };
    mainGenHandler.doGenerate = wrapper;
    installedGenerationWrapper = wrapper;
    return true;
};

export const hasHostInputBrowser = (): boolean =>
    typeof inputBrowserHelper !== "undefined" && Boolean(inputBrowserHelper);

export const isHostReusableMediaPath = (value: string): boolean =>
    typeof isValidMediaPath === "function" && isValidMediaPath(value);

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
