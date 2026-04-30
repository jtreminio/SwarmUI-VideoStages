import { applyVideoStagesPresetDimensionsBeforeGenerate } from "./dimensionsDropdown";
import { getClipsInput, isVideoStagesEnabled } from "./swarmInputs";
import { validateClips } from "./validation";

export type GenerateWrapApi = {
    tryWrap: () => void;
    startRetry: (intervalMs?: number) => void;
};

export const createGenerateWrap = (deps: {
    getClips: () => import("./types").Clip[];
}): GenerateWrapApi => {
    let genButtonWrapped = false;
    let genWrapInterval: ReturnType<typeof setInterval> | null = null;

    const tryWrap = (): void => {
        if (genButtonWrapped) {
            return;
        }
        if (
            typeof mainGenHandler === "undefined" ||
            !mainGenHandler ||
            typeof mainGenHandler.doGenerate !== "function"
        ) {
            return;
        }

        const original = mainGenHandler.doGenerate.bind(mainGenHandler);
        mainGenHandler.doGenerate = (...args: unknown[]) => {
            const clipsInput = getClipsInput();
            if (!clipsInput) {
                return original(...args);
            }
            if (!isVideoStagesEnabled()) {
                return original(...args);
            }

            const clips = deps.getClips();
            const errors = validateClips(clips);
            if (errors.length > 0) {
                showError(errors[0]);
                return;
            }

            applyVideoStagesPresetDimensionsBeforeGenerate();
            return original(...args);
        };
        mainGenHandler.doGenerate.__videoStagesWrapped = true;
        genButtonWrapped = true;
    };

    const startRetry = (intervalMs = 250): void => {
        if (genWrapInterval) {
            return;
        }

        const runTryWrap = () => {
            try {
                tryWrap();
                if (
                    typeof mainGenHandler !== "undefined" &&
                    mainGenHandler &&
                    typeof mainGenHandler.doGenerate === "function" &&
                    mainGenHandler.doGenerate.__videoStagesWrapped
                ) {
                    if (genWrapInterval) {
                        clearInterval(genWrapInterval);
                        genWrapInterval = null;
                    }
                }
            } catch {}
        };

        runTryWrap();
        genWrapInterval = setInterval(runTryWrap, intervalMs);
    };

    return { tryWrap, startRetry };
};
