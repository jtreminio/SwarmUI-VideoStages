import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { buildArchitectureModelCatalog } from "../architectures/catalog";
import { buildAudioSourceOptions } from "../audioSource";
import {
    resetBrowserMediaStoreForTests,
    storeBrowserMedia,
} from "../browserMediaStore";
import { mediaPreviewSrc } from "../constants";
import { materializeGenerationMedia } from "../generationMedia";
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
import { installHostGenerationInputProjector } from "./swarmUiAdapters";

afterEach(() => {
    jest.useRealTimers();
    resetBrowserMediaStoreForTests();
    setVideoStagesHostBridgeForTests(null);
    const globals = globalThis as typeof globalThis & {
        copyText?: unknown;
        currentMetadataVal?: unknown;
        getGenInput?: unknown;
        getImageFullSrc?: unknown;
        mainGenHandler?: unknown;
        genericRequest?: unknown;
        registerMediaButton?: unknown;
        showError?: unknown;
    };
    Reflect.deleteProperty(globals, "copyText");
    Reflect.deleteProperty(globals, "currentMetadataVal");
    Reflect.deleteProperty(globals, "getGenInput");
    Reflect.deleteProperty(globals, "getImageFullSrc");
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

    it("materializes media on the bridge's direct Generate route", () => {
        const payload = "data:image/png;base64,DIRECT_BRIDGE";
        const handle = storeBrowserMedia(payload);
        const carrier = JSON.stringify({
            clips: [{ initVideo: { data: handle, fileName: "direct.mp4" } }],
        });
        const sent: Record<string, unknown>[] = [];
        const handler = {
            doGenerate: (
                overrides: Record<string, unknown>,
                _preOverrides: Record<string, unknown>,
                postCollect?: (input: Record<string, unknown>) => void,
            ): void => {
                const input = { ...overrides };
                postCollect?.(input);
                sent.push(input);
            },
        };
        const globals = globalThis as typeof globalThis & {
            mainGenHandler?: typeof handler;
        };
        globals.mainGenHandler = handler;

        createDefaultVideoStagesHostBridge().generate({
            videostages: carrier,
        });

        expect(`${sent[0].videostages}`).toContain("DIRECT_BRIDGE");
        expect(carrier).toContain(handle);
        expect(carrier).not.toContain("DIRECT_BRIDGE");
    });

    it("materializes browser media only in the outbound Generate input", () => {
        const payload = "data:image/png;base64,GENERATE_ONLY";
        const handle = storeBrowserMedia(payload);
        const carrier = JSON.stringify({
            clips: [
                {
                    keyframes: [
                        {
                            uploadedImage: {
                                data: handle,
                                fileName: "reference.png",
                            },
                        },
                    ],
                },
            ],
        });
        const sent: Record<string, unknown>[] = [];
        const handler = {
            getGenInput: () => ({ videostages: carrier }),
            doGenerate: (
                _overrides: Record<string, unknown> = {},
                _preOverrides: Record<string, unknown> = {},
                postCollect?: (input: Record<string, unknown>) => void,
            ): void => {
                const actualInput = { videostages: carrier };
                postCollect?.(actualInput);
                sent.push(actualInput);
            },
        };
        const globals = globalThis as typeof globalThis & {
            mainGenHandler?: typeof handler;
        };
        globals.mainGenHandler = handler;
        installHostGenerationInputProjector(materializeGenerationMedia);

        handler.doGenerate();

        expect(`${sent[0].videostages}`).toContain("GENERATE_ONLY");
        expect(carrier).not.toContain("GENERATE_ONLY");
        expect(carrier).toContain(handle);
    });

    it("materializes browser media for host tools that collect Generate-tab inputs", () => {
        const payload = "data:image/png;base64,WORKFLOW_IMPORT";
        const handle = storeBrowserMedia(payload);
        const carrier = JSON.stringify({
            clips: [
                {
                    references: [
                        {
                            kind: "image",
                            uploadedMedia: {
                                data: handle,
                                fileName: "reference.png",
                            },
                        },
                    ],
                },
            ],
        });
        const getGenInput = jest.fn(() => ({ videostages: carrier }));
        const globals = globalThis as typeof globalThis & {
            getGenInput?: typeof getGenInput;
            mainGenHandler?: {
                doGenerate: (...args: unknown[]) => void;
                getGenInput: typeof getGenInput;
            };
        };
        globals.getGenInput = getGenInput;
        globals.mainGenHandler = {
            doGenerate: jest.fn(),
            getGenInput,
        };

        installHostGenerationInputProjector(materializeGenerationMedia);

        const actualInput = globals.getGenInput();
        expect(`${actualInput.videostages}`).toContain("WORKFLOW_IMPORT");
        expect(carrier).toContain(handle);
        expect(carrier).not.toContain("WORKFLOW_IMPORT");
    });

    it("retries a late host handler and rewraps a replaced Generate function", () => {
        jest.useFakeTimers();
        const globals = globalThis as typeof globalThis & {
            mainGenHandler?: {
                doGenerate: (...args: unknown[]) => void;
            };
        };
        delete globals.mainGenHandler;
        expect(
            installHostGenerationInputProjector(materializeGenerationMedia),
        ).toBe(false);

        const payload = "data:image/png;base64,LATE_HANDLER";
        const handle = storeBrowserMedia(payload);
        const carrier = JSON.stringify({
            clips: [{ initVideo: { data: handle, fileName: "late.mp4" } }],
        });
        const sent: Record<string, unknown>[] = [];
        const send = (...args: unknown[]): void => {
            const input = { videostages: carrier };
            const postCollect = args[2];
            if (typeof postCollect === "function") {
                postCollect(input);
            }
            sent.push(input);
        };
        globals.mainGenHandler = { doGenerate: send };

        jest.runOnlyPendingTimers();
        expect(globals.mainGenHandler.doGenerate).not.toBe(send);

        const replacement = (...args: unknown[]): void => send(...args);
        globals.mainGenHandler.doGenerate = replacement;
        expect(
            installHostGenerationInputProjector(materializeGenerationMedia),
        ).toBe(true);
        expect(globals.mainGenHandler.doGenerate).not.toBe(replacement);

        globals.mainGenHandler.doGenerate();

        expect(`${sent[0].videostages}`).toContain("LATE_HANDLER");
    });

    it("shows an error and aborts Generate when browser media is gone", () => {
        const handle = storeBrowserMedia(
            "data:image/png;base64,MISSING_AT_GENERATE",
        );
        const carrier = JSON.stringify({
            clips: [
                {
                    references: [
                        {
                            uploadedMedia: {
                                data: handle,
                                fileName: "missing.png",
                            },
                        },
                    ],
                },
            ],
        });
        resetBrowserMediaStoreForTests();
        const sent: Record<string, unknown>[] = [];
        const handler = {
            doGenerate: (...args: unknown[]): void => {
                const input = { videostages: carrier };
                const postCollect = args[2];
                if (typeof postCollect === "function") {
                    postCollect(input);
                }
                sent.push(input);
            },
        };
        const showError = jest.fn();
        const globals = globalThis as typeof globalThis & {
            mainGenHandler?: typeof handler;
            showError?: typeof showError;
        };
        globals.mainGenHandler = handler;
        globals.showError = showError;
        installHostGenerationInputProjector(materializeGenerationMedia);

        expect(() => handler.doGenerate()).toThrow(
            "Reselect browser-local VideoStages media: missing.png.",
        );
        expect(sent).toEqual([]);
        expect(showError).toHaveBeenCalledWith(
            expect.stringContaining("missing.png"),
        );
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

    it("reads metadata from the media matching the selected source", async () => {
        const globals = globalThis as typeof globalThis & {
            currentMetadataVal?: string;
        };
        globals.currentMetadataVal = "stale metadata";
        document.body.innerHTML = `
            <video id="current_image_img"
                data-src="/Output/older.mp4"
                data-metadata="older metadata"></video>
            <div class="image-block"
                data-src="/Output/selected.mp4"
                data-metadata="selected metadata"></div>`;

        const metadata =
            await createDefaultVideoStagesHostBridge().getMediaMetadata(
                "/Output/selected.mp4",
            );

        expect(metadata).toBe("selected metadata");
    });

    it("loads metadata for an unselected history source", async () => {
        const getImageFullSrc = jest.fn(() => "raw/2026-08-16/selected.mp4");
        const genericRequest = jest.fn(
            (
                url: string,
                data: Record<string, unknown>,
                callback: (response: unknown) => void,
            ) => {
                expect(url).toBe("ListImages");
                expect(data).toEqual({
                    path: "raw/2026-08-16",
                    depth: 0,
                    sortBy: "Name",
                    sortReverse: false,
                });
                callback({
                    files: [
                        {
                            src: "selected.mp4",
                            metadata: "selected metadata",
                        },
                    ],
                });
            },
        );
        const globals = globalThis as typeof globalThis & {
            currentMetadataVal?: string;
            genericRequest?: typeof genericRequest;
            getImageFullSrc?: typeof getImageFullSrc;
        };
        globals.currentMetadataVal = "stale metadata";
        globals.genericRequest = genericRequest;
        globals.getImageFullSrc = getImageFullSrc;
        document.body.innerHTML = `
            <video id="current_image_img"
                data-src="/View/output/raw/2026-08-16/selected.mp4"
                data-metadata="stale metadata"></video>`;

        const metadata =
            await createDefaultVideoStagesHostBridge().getMediaMetadata(
                "/View/output/raw/2026-08-16/selected.mp4",
            );

        expect(metadata).toBe("selected metadata");
        expect(genericRequest).toHaveBeenCalledTimes(1);
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
