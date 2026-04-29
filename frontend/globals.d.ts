declare const mainGenHandler: {
    doGenerate: ((...args: unknown[]) => void) & {
        __videoStagesWrapped?: boolean;
    };
};

declare function showError(message: string): void;
declare function triggerChangeFor(element: HTMLElement): void;
declare function doToggleEnable(prefix: string): void;
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
declare function makeGenericPopover(
    id: string,
    name: string,
    type: string,
    description: string,
    example: string,
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
declare function makeAudioInput(
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
declare let postParamBuildSteps: (() => void)[] | undefined;

declare const promptTabComplete:
    | {
          registerPrefix: (
              prefix: string,
              description: string,
              dataProvider: () => string[],
              insertable?: boolean,
          ) => void;
      }
    | undefined;

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
              modelClass?: {
                  id?: string;
                  compatClass?: { id?: string; isText2Video?: boolean };
              };
          } | null;
          compatClasses?: Record<
              string,
              { id?: string; isText2Video?: boolean }
          >;
      }
    | undefined;

declare const currentModelHelper:
    | {
          curCompatClass?: string;
      }
    | undefined;

interface Window {
    /** When true, VideoStages frontend logs reaction points to the console (see debugLog.ts). */
    __VIDEO_STAGES_DEBUG__?: boolean;
    base2editStageRegistry?: Base2EditStageRegistry;
    acestepfunTrackRegistry?: AceStepFunTrackRegistry;
}
