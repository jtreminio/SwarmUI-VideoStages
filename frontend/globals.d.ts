declare const mainGenHandler: {
    doGenerate: ((...args: unknown[]) => void) & {
        __videoStagesWrapped?: boolean;
    };
};

declare function showError(message: string): void;
declare function triggerChangeFor(element: HTMLElement): void;
declare function doToggleEnable(prefix: string): void;
declare let doToggleGroup: ((id: string) => void) & {
    __toggleableGroupReuseGuardWrapped?: boolean;
};
declare function findParentOfClass(
    element: Element,
    className: string,
): HTMLElement;
declare function getParamById(
    id: string,
): { values?: string[]; value_names?: string[] } | null;

declare function makeNumberInput(
    featureid: string,
    id: string,
    paramid: string,
    name: string,
    description: string,
    value: string | number,
    min: string | number,
    max: string | number,
    step?: string | number,
    format?: "small" | "big" | "seed",
    toggles?: boolean,
    popover_button?: boolean,
): string;
declare function makeSliderInput(
    featureid: string,
    id: string,
    paramid: string,
    name: string,
    description: string,
    value: string | number,
    min: string | number,
    max: string | number,
    view_min?: string | number,
    view_max?: string | number,
    step?: string | number,
    isPot?: boolean,
    toggles?: boolean,
    popover_button?: boolean,
): string;
declare function makeDropdownInput(
    featureid: string,
    id: string,
    paramid: string,
    name: string,
    description: string,
    values: string[],
    defaultVal: string,
    toggles?: boolean,
    popover_button?: boolean,
    alt_names?: string[] | null,
    reparse_alt_names?: boolean,
): string;
declare function makeImageInput(
    featureid: string,
    id: string,
    paramid: string,
    name: string,
    description: string,
    toggles?: boolean,
    popover_button?: boolean,
    can_upload?: boolean,
    show_input_browser_button?: boolean,
): string;
declare function makeTextInput(
    featureid: string,
    id: string,
    paramid: string,
    name: string,
    description: string,
    value: string,
    format: string,
    placeholder: string,
    toggles?: boolean,
    genPopover?: boolean,
    popover_button?: boolean,
): string;
declare function makeCheckboxInput(
    featureid: string,
    id: string,
    paramid: string,
    name: string,
    description: string,
    value: boolean | string,
    toggles?: boolean,
    genPopover?: boolean,
    popover_button?: boolean,
): string;
declare function autoNumberWidth(elem: HTMLElement): void;
declare function autoSelectWidth(elem: HTMLElement): void;
declare function enableSlidersIn(elem: HTMLElement): void;
declare function clearMediaFileInput(elem: HTMLInputElement): void;
declare function setMediaFileDirect(
    elem: HTMLInputElement,
    src: string,
    type: string,
    name: string,
    longName?: string | null,
    callback?: (() => void) | null,
): void;
declare let copy_current_image_params: (() => void) & {
    __videoStagesWrapped?: boolean;
};
declare let currentMetadataVal: string | null | undefined;
declare function interpretMetadata(metadata: string): string;

declare let postParamBuildSteps: (() => void)[] | undefined;

interface Base2EditStageSnapshot {
    enabled: boolean;
    stageCount: number;
    refs: string[];
}

interface Base2EditStageRegistry {
    getSnapshot: () => Base2EditStageSnapshot;
}

interface AceStepFunTrackSnapshot {
    enabled: boolean;
    trackCount: number;
    refs: string[];
}

interface AceStepFunTrackRegistry {
    getSnapshot: () => AceStepFunTrackSnapshot;
}

declare const modelsHelpers:
    | {
          getDataFor?: (
              category: string,
              modelName: string,
          ) => {
              modelClass?: { compatClass?: { isText2Video?: boolean } };
          } | null;
          compatClasses?: Record<string, { isText2Video?: boolean }>;
      }
    | undefined;

declare const currentModelHelper:
    | {
          curCompatClass?: string;
      }
    | undefined;

interface Window {
    base2editStageRegistry?: Base2EditStageRegistry;
    acestepfunTrackRegistry?: AceStepFunTrackRegistry;
}
