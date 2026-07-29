import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { buildArchitectureModelCatalog } from "../architectures/catalog";
import { buildAudioSourceOptions } from "../audioSource";
import { mediaPreviewSrc } from "../constants";
import {
    getDataInput,
    getPromptInput,
    notifyCarrierChanged,
    readDataParam,
    writeDataParam,
} from "../swarmInputs";
import { createDefaultVideoStagesHostBridge } from "./defaultVideoStagesHostBridge";
import {
    setVideoStagesHostBridgeForTests,
    type VideoStagesHostBridge,
} from "./index";

afterEach(() => {
    setVideoStagesHostBridgeForTests(null);
});

describe("VideoStagesHostBridge compatibility facades", () => {
    it("routes carriers, registries, and media paths through an injected bridge", () => {
        const data = document.createElement("textarea");
        data.value = "before";
        const prompt = document.createElement("textarea");
        prompt.value = "prompt";
        const notifyChanged = jest.fn();
        const base = createDefaultVideoStagesHostBridge();
        const fake: VideoStagesHostBridge = {
            ...base,
            getTextInput: (id) => {
                if (id === "input_videostages") return data;
                if (id === "input_prompt") return prompt;
                return null;
            },
            notifyChanged,
            getAceStepFunRegistry: () => ({
                enabled: true,
                refs: ["audio3"],
            }),
            getMediaOutputPrefix: () => "/host/output",
        };
        setVideoStagesHostBridgeForTests(fake);

        expect(getDataInput()).toBe(data);
        expect(getPromptInput()).toBe(prompt);
        expect(readDataParam()).toBe("before");
        writeDataParam("after");
        notifyCarrierChanged();
        expect(data.value).toBe("after");
        expect(notifyChanged).toHaveBeenNthCalledWith(1, data);
        expect(notifyChanged).toHaveBeenNthCalledWith(2, prompt, true);

        expect(
            buildAudioSourceOptions().map((option) => option.value),
        ).toContain("audio3");
        expect(
            buildArchitectureModelCatalog(
                ["metadata-backed-model"],
                ["Metadata-backed model"],
            ).entries[0],
        ).toMatchObject({
            architectureId: null,
            modelProfileId: null,
        });
        expect(mediaPreviewSrc("clip.mp4")).toBe("/host/output/clip.mp4");
    });

    it("lets the default bridge add and remove the host param-refresh hook", () => {
        const globals = globalThis as typeof globalThis & {
            refreshParamsExtra?: (() => unknown)[];
        };
        globals.refreshParamsExtra = [];
        const hook = jest.fn();
        const cleanup =
            createDefaultVideoStagesHostBridge().addParamRefreshHook(hook);

        expect(globals.refreshParamsExtra).toEqual([hook]);
        cleanup?.();
        expect(globals.refreshParamsExtra).toEqual([]);
    });
});
