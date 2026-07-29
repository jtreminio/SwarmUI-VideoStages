export interface HostOptionList {
    values: string[];
    labels: string[];
}

export interface HostRegistrySnapshot {
    enabled?: boolean;
    refs?: unknown[];
}

export type PromptPrefixExamples = () => string[];

export interface VideoStagesHostBridge {
    hasElement(id: string): boolean;
    getTextInput(id: string): HTMLInputElement | HTMLTextAreaElement | null;
    getInput(id: string): HTMLInputElement | null;
    getRootVideoFpsInput(): HTMLInputElement | null;
    getSelect(id: string): HTMLSelectElement | null;
    getSelectOptions(select: HTMLSelectElement | null): HostOptionList;
    getParamOptions(paramId: string): HostOptionList | null;
    notifyChanged(
        element: HTMLElement,
        suppressPromptCompletion?: boolean,
    ): void;

    getBase2EditRegistry(): HostRegistrySnapshot | null;
    getAceStepFunRegistry(): HostRegistrySnapshot | null;
    getLoraDefaultWeight(modelName: string): number | null;
    requestJson(url: string, data?: Record<string, unknown>): Promise<unknown>;

    registerPromptPrefix(
        prefix: string,
        description: string,
        examples: PromptPrefixExamples,
        isMulti: boolean,
    ): void;
    addPostParamBuildStep(step: () => void): boolean;
    addParamRefreshHook(hook: () => unknown): (() => void) | null;

    getMediaOutputPrefix(): string;
    createSourceVideoElement(): HTMLVideoElement;
    enableSliders(element: HTMLElement): void;

    registerRefineVideoButton(
        onSelect: (src: string) => void,
        description: string,
    ): void;
    getCurrentMediaMetadata(): string | null;
    interpretMediaMetadata(metadata: string): string | null;
    showError(message: string): void;
    toDataUrl(src: string): Promise<string>;
    generate(inputOverrides: Record<string, unknown>): void;
}
