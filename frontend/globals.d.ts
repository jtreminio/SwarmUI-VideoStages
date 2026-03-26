declare const mainGenHandler: {
    doGenerate: ((...args: unknown[]) => void) & { __videoStagesWrapped?: boolean };
};

declare function showError(message: string): void;
declare function triggerChangeFor(element: HTMLElement): void;
declare function doToggleEnable(prefix: string): void;
declare function findParentOfClass(element: Element, className: string): HTMLElement;
declare function getHtmlForParam(param: Record<string, any>, prefix: string): { html: string; runnable: () => void };
declare function getParamById(id: string): { values?: string[]; value_names?: string[] } | null;

declare let postParamBuildSteps: (() => void)[] | undefined;
