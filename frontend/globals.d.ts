declare const mainGenHandler: {
    doGenerate: (...args: unknown[]) => void;
};

declare function showError(message: string): void;
declare function triggerChangeFor(element: HTMLElement): void;
declare function textPromptAddKeydownHandler(element: HTMLElement): void;
declare function getParamById(
    id: string,
): { values?: string[]; value_names?: string[] } | null;
declare function cleanParamName(name: string | null): string | null;

declare function makeSliderInput(
    featureid: string | null,
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
declare function enableSlidersIn(elem: HTMLElement): void;
declare let postParamBuildSteps: (() => void)[] | undefined;
// Callbacks run after the host refreshes model/lora/dropdown values (the
// refresh button in the model browsers). Unlike postParamBuildSteps, this does
// NOT re-run our init, so we hook it to re-read the fresh dropdown options.
declare let refreshParamsExtra: (() => unknown)[] | undefined;

declare function getImageOutPrefix(): string;

declare function toDataURL(
    url: string,
    callback: (dataUrl: string) => void,
): void;

declare const inputBrowserHelper:
    | {
          openInputBrowser(inputElemId: string, type: string[]): void;
      }
    | undefined;

declare function doPopover(id: string, e?: Event): void;

declare let currentMetadataVal: string | null;

declare function interpretMetadata(metadata: string | null): string | null;

declare function registerMediaButton(
    name: string,
    action: (src: string) => void,
    title?: string,
    mediaTypes?: string[] | null,
    isDefault?: boolean,
    showInHistory?: boolean,
    href?: string | null,
    is_download?: boolean,
    can_multi?: boolean,
    multi_only?: boolean,
): void;

declare const promptTabComplete:
    | {
          registerPrefix: (
              prefix: string,
              description: string,
              dataProvider: () => string[],
              insertable?: boolean,
          ) => void;
          /**
           * Host guard flag (prompttools.js): when true, `onInput` bails before
           * opening the tab-complete popover. We raise it around our own
           * programmatic prompt-box writes so a synthetic `input` event can
           * never pop the keyboard-driven suggestion UI.
           */
          blockInput?: boolean;
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
              lora_default_weight?: string | number;
          } | null;
      }
    | undefined;

declare const sdLoraBrowser:
    | {
          models?: Record<
              string,
              {
                  data?: {
                      lora_default_weight?: string | number;
                  };
              }
          >;
      }
    | undefined;

declare const loraHelper:
    | {
          loraWeightPref?: Record<string, string | number>;
      }
    | undefined;

interface Window {
    /** When true, VideoStages frontend logs reaction points to the console (see debugLog.ts). */
    __VIDEO_STAGES_DEBUG__?: boolean;
    parameter_remaps?: Record<string, string>;
    base2editStageRegistry?: Base2EditStageRegistry;
    acestepfunTrackRegistry?: AceStepFunTrackRegistry;
}

declare function genericRequest(
    url: string,
    data: Record<string, unknown>,
    callback: (data: unknown) => void,
    depth?: number,
    errorHandle?: (error: unknown) => void,
): void;

declare const browserUtil: {
    makeVisible(elem: Element | Document): void;
};

interface GenTabLayoutLike {
    managedTabs: MovableGenTab[];
    managedTabContainers: Element[];
    reapplyPositions(): void;
}

declare const genTabLayout: GenTabLayoutLike;

declare class MovableGenTab {
    constructor(navLink: Element, handler: GenTabLayoutLike);
    contentElem: HTMLElement;
    navElem: HTMLElement;
    update(): void;
}

/** Core websocket API caller (site.js); used for DoModelDownloadWS streaming downloads. */
declare function makeWSRequest(
    url: string,
    in_data: Record<string, unknown>,
    callback: (data: Record<string, unknown>) => void,
    depth?: number,
    errorHandle?: ((error: string) => void) | null,
    onOpenHandle?: ((socket: WebSocket) => void) | null,
): void;

/** Core param refresh (genpage); re-lists models server-side and rebuilds param dropdowns. */
declare function refreshParameterValues(callAlways?: boolean): void;
