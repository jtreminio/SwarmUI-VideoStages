import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { buildAudioSourceOptions } from "../audioSource";
import { mediaPreviewSrc } from "../constants";
import { isLtxVideoModelValue } from "../ltxCapabilities";
import {
    getDataInput,
    getPromptInput,
    notifyCarrierChanged,
    readDataParam,
    writeDataParam,
} from "../swarmInputs";
import { createDefaultLtxHostBridge } from "./defaultLtxHostBridge";
import { type LtxHostBridge, setLtxHostBridgeForTests } from "./index";

afterEach(() => {
    setLtxHostBridgeForTests(null);
});

describe("LtxHostBridge compatibility facades", () => {
    it("routes carriers, registries, model metadata, and media paths through an injected bridge", () => {
        const data = document.createElement("textarea");
        data.value = "before";
        const prompt = document.createElement("textarea");
        prompt.value = "prompt";
        const notifyChanged = jest.fn();
        const base = createDefaultLtxHostBridge();
        const fake: LtxHostBridge = {
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
            getModelCompatId: () => "ltxv2",
            getMediaOutputPrefix: () => "/host/output",
        };
        setLtxHostBridgeForTests(fake);

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
        expect(isLtxVideoModelValue("metadata-backed-model")).toBe(true);
        expect(mediaPreviewSrc("clip.mp4")).toBe("/host/output/clip.mp4");
    });

    it("lets the default bridge add and remove the host param-refresh hook", () => {
        const globals = globalThis as typeof globalThis & {
            refreshParamsExtra?: (() => unknown)[];
        };
        globals.refreshParamsExtra = [];
        const hook = jest.fn();
        const cleanup = createDefaultLtxHostBridge().addParamRefreshHook(hook);

        expect(globals.refreshParamsExtra).toEqual([hook]);
        cleanup?.();
        expect(globals.refreshParamsExtra).toEqual([]);
    });
});
