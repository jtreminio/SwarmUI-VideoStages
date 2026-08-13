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
    const globals = globalThis as typeof globalThis & {
        copyText?: unknown;
        mainGenHandler?: unknown;
        genericRequest?: unknown;
        registerMediaButton?: unknown;
        showError?: unknown;
    };
    Reflect.deleteProperty(globals, "copyText");
    delete globals.mainGenHandler;
    Reflect.deleteProperty(globals, "genericRequest");
    Reflect.deleteProperty(globals, "registerMediaButton");
    Reflect.deleteProperty(globals, "showError");
    document.body.innerHTML = "";
});

describe("VideoStagesHostBridge compatibility facades", () => {
    it("registers Refine Video as a primary Generate-tab button", () => {
        const registerMediaButton = jest.fn();
        const globals = globalThis as typeof globalThis & {
            registerMediaButton?: typeof registerMediaButton;
        };
        globals.registerMediaButton = registerMediaButton;
        const bridge = createDefaultVideoStagesHostBridge();
        const refine = jest.fn();

        bridge.registerRefineVideoButton(refine, "refine description");

        expect(registerMediaButton.mock.calls).toEqual([
            ["Refine Video", refine, "refine description", ["video"], true],
        ]);
    });

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

    it("adds requested extra metadata after the host collects generation inputs", () => {
        const doGenerate =
            jest.fn<
                (
                    overrides: Record<string, unknown>,
                    preOverrides: Record<string, unknown>,
                    postCollect?: (
                        actualInput: Record<string, unknown>,
                    ) => void,
                ) => void
            >();
        const globals = globalThis as typeof globalThis & {
            mainGenHandler?: { doGenerate: typeof doGenerate };
        };
        globals.mainGenHandler = { doGenerate };

        createDefaultVideoStagesHostBridge().generate({
            prompt: "parsed prompt",
            extra_metadata: {
                original_prompt: "<wildcard:animal>",
                used_wildcards: ["animal"],
            },
        });

        expect(doGenerate).toHaveBeenCalledTimes(1);
        const [overrides, preOverrides, postCollect] = doGenerate.mock.calls[0];
        expect(overrides).toEqual({ prompt: "parsed prompt" });
        expect(preOverrides).toEqual({});
        const actualInput: Record<string, unknown> = {
            prompt: "parsed prompt",
            extra_metadata: { current_ui_value: "kept" },
        };
        expect(postCollect).toEqual(expect.any(Function));
        postCollect?.(actualInput);
        expect(actualInput.extra_metadata).toEqual({
            current_ui_value: "kept",
            original_prompt: "<wildcard:animal>",
            used_wildcards: ["animal"],
        });
    });

    it("reads the selected Generate-tab video source", () => {
        document.body.innerHTML =
            '<video id="current_image_img" data-src="/Output/refine.mp4"></video>';
        const bridge = createDefaultVideoStagesHostBridge();

        expect(bridge.getCurrentVideoSource()).toBe("/Output/refine.mp4");

        document.body.innerHTML =
            '<img id="current_image_img" data-src="/Output/still.png">';
        expect(bridge.getCurrentVideoSource()).toBeNull();
    });

    it("loads and saves collected overrides without generating", async () => {
        const doGenerate = jest.fn();
        const getGenInput = jest.fn<
            (
                overrides: Record<string, unknown>,
                preOverrides: Record<string, unknown>,
            ) => Record<string, unknown>
        >((overrides) => ({
            model: "current-model.safetensors",
            ...overrides,
            extra_metadata: { current_ui_value: "kept" },
        }));
        const requests: [string, Record<string, unknown>][] = [];
        const genericRequest = jest.fn(
            (
                url: string,
                data: Record<string, unknown>,
                callback: (response: unknown) => void,
            ) => {
                requests.push([url, data]);
                if (url === "ComfyGetGeneratedWorkflow") {
                    callback({
                        workflow: '{"1":{"class_type":"PreviewImage"}}',
                    });
                    return;
                }
                callback({
                    success: true,
                    payloadPath: "/server/refine_payload.json",
                    workflowPath: "/server/refine_workflow.json",
                    payloadLocalPath: "~/swarm/refine_payload.json",
                    workflowLocalPath: "~/swarm/refine_workflow.json",
                });
            },
        );
        const copyText = jest.fn();
        const showError = jest.fn();
        const globals = globalThis as typeof globalThis & {
            copyText?: typeof copyText;
            mainGenHandler?: {
                doGenerate: typeof doGenerate;
                getGenInput: typeof getGenInput;
            };
            genericRequest?: typeof genericRequest;
            showError?: typeof showError;
        };
        globals.copyText = copyText;
        globals.mainGenHandler = { doGenerate, getGenInput };
        globals.genericRequest = genericRequest;
        globals.showError = showError;

        const tab = document.createElement("button");
        tab.id = "maintab_comfyworkflow";
        const tabClick = jest.fn();
        tab.addEventListener("click", tabClick);
        document.body.appendChild(tab);
        const frame = document.createElement("iframe");
        frame.id = "comfy_workflow_frame";
        document.body.appendChild(frame);
        const loadApiJson = jest.fn((_workflow: unknown) => {
            throw new Error("editor load failed");
        });
        const cloneObject = jest.fn((workflow: unknown) => workflow);
        Object.assign(frame.contentWindow as Window, {
            app: { loadApiJson },
            LiteGraph: { cloneObject },
        });

        await createDefaultVideoStagesHostBridge().sendToComfyUiAndSave({
            videostages: "refine-document",
            images: 1,
            extra_metadata: { original_prompt: "authored prompt" },
        });

        expect(doGenerate).not.toHaveBeenCalled();
        expect(getGenInput).toHaveBeenCalledWith(
            { videostages: "refine-document", images: 1 },
            {},
        );
        expect(requests).toEqual([
            [
                "ComfyGetGeneratedWorkflow",
                {
                    model: "current-model.safetensors",
                    videostages: "refine-document",
                    images: 1,
                    extra_metadata: {
                        current_ui_value: "kept",
                        original_prompt: "authored prompt",
                    },
                },
            ],
            [
                "WhatTheDuckSaveComfyWorkflow",
                {
                    payload: JSON.stringify({
                        model: "current-model.safetensors",
                        videostages: "refine-document",
                        images: 1,
                        extra_metadata: {
                            current_ui_value: "kept",
                            original_prompt: "authored prompt",
                        },
                    }),
                    workflow: '{"1":{"class_type":"PreviewImage"}}',
                },
            ],
        ]);
        expect(tabClick).toHaveBeenCalledTimes(1);
        expect(loadApiJson).toHaveBeenCalledWith({
            "1": { class_type: "PreviewImage" },
        });
        expect(showError).toHaveBeenCalledWith(
            expect.stringContaining("editor load failed") as unknown as string,
        );
        expect(copyText).toHaveBeenCalledWith(
            "Payload: ~/swarm/refine_payload.json, " +
                "Generated Workflow: ~/swarm/refine_workflow.json",
        );
    });
});
