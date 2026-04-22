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
declare function getHtmlForParam(
    param: Record<string, any>,
    prefix: string,
): { html: string; runnable: () => void };
declare function getParamById(
    id: string,
): { values?: string[]; value_names?: string[] } | null;
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
}
