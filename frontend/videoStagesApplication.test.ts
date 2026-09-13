import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { resetArchitectureCatalogForTests } from "./__test_helpers__/architectureCatalog";
import { testArchitectureCatalogDto } from "./__test_helpers__/architectureFixtures";
import {
    mountCheckbox,
    mountPromptBox,
    mountSelect,
    mountVideoStagesData,
} from "./__test_helpers__/dom";
import { loadAuthoritativeArchitectureCatalog } from "./architectures/catalog";
import { createClipEditor } from "./clipEditor/clipEditor";
import { EDITOR_BODY_ID, EDITOR_TAB_ID } from "./clipEditor/tab";
import { setVideoStagesHostBridgeForTests } from "./host";
import { createDefaultVideoStagesHostBridge } from "./host/defaultVideoStagesHostBridge";
import {
    __resetPersistenceForTests,
    getState,
    saveClips,
} from "./persistence/repository";
import {
    getSelection,
    resetSelectionForTests,
    setSelection,
} from "./selection";
import { TIMELINE_AUTHORING_SETTINGS_CHANGED } from "./timelineAuthoringSettings";
import { createVideoStagesApplication } from "./videoStagesApplication";

const payload = (duration: number) => ({
    schemaVersion: 9,
    clips: [
        {
            duration,
            stages: [
                { model: "ltx-2.3.safetensors", modelProfileId: "ltx-2.3" },
            ],
            frameRefs: [],
        },
    ],
});
const required = <T>(value: T | null | undefined): T => {
    if (value == null) throw new Error("Required test element is missing");
    return value;
};
const root = () => required(document.getElementById(EDITOR_BODY_ID));
const cards = () => root().querySelectorAll('[data-testid="clip-card"]');
const settle = async () => {
    for (let i = 0; i < 12; i++) await Promise.resolve();
};

const deferred = () => {
    let resolve: (value: unknown) => void = () => undefined;
    let reject: (reason: unknown) => void = () => undefined;
    const promise = new Promise<unknown>((accept, fail) => {
        resolve = accept;
        reject = fail;
    });
    // Unconsumed test promises must reach assertions instead of aborting Jest.
    void promise.catch(() => undefined);
    return { promise, resolve, reject };
};
const catalogWithModels = (...names: string[]) => {
    const catalog = testArchitectureCatalogDto();
    catalog.models = names.map((modelName) => ({
        ...catalog.models[0],
        modelName,
    }));
    return catalog;
};
const modelNames = () =>
    Array.from(
        required(
            root().querySelector<HTMLSelectElement>(
                '[data-section="stages"] select',
            ),
        ).options,
        (option) => option.value,
    );

describe("VideoStages application", () => {
    let app: ReturnType<typeof createVideoStagesApplication> | undefined;
    let editor: ReturnType<typeof createClipEditor>;
    beforeEach(() => {
        jest.useFakeTimers();
        resetArchitectureCatalogForTests();
        __resetPersistenceForTests();
        resetSelectionForTests();
        localStorage.clear();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(),
        });
        document.body.innerHTML =
            '<ul id="bottombartabcollection"></ul><div id="t2i_bottom_bar_content"></div>';
        mountPromptBox("");
        mountVideoStagesData(payload(4));
        mountSelect("input_videomodel", {
            value: "ltx-2.3.safetensors",
            options: ["ltx-2.3.safetensors"],
        });
        mountCheckbox("input_group_content_videostages_toggle", {
            checked: true,
        });
    });
    afterEach(() => {
        app?.dispose();
        app = undefined;
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests(null);
        jest.useRealTimers();
        jest.restoreAllMocks();
        window.history.replaceState(null, "", "/");
    });
    const start = () => {
        editor = createClipEditor();
        app = createVideoStagesApplication(editor);
        app.init();
    };

    it("loads the catalog before presenting the saved clip for editing", async () => {
        start();
        expect(
            root()
                .querySelector('[data-catalog-status="loading"]')
                ?.getAttribute("aria-busy"),
        ).toBe("true");
        await settle();
        expect(cards()).toHaveLength(1);
        expect(
            root()
                .querySelector('[data-section="stages"] select')
                ?.getAttribute("class"),
        ).toContain("auto-dropdown");
    });

    it("recovers authoring through the catalog retry action", async () => {
        jest.spyOn(console, "warn").mockImplementation(() => undefined);
        const requestJson = jest
            .fn<() => Promise<unknown>>()
            .mockRejectedValueOnce(new Error("offline"))
            .mockResolvedValue(testArchitectureCatalogDto());
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
        });
        start();
        await settle();
        const retry = required(
            root().querySelector<HTMLButtonElement>(".vst-catalog-retry"),
        );
        expect(retry.textContent).toBe("Retry");
        retry.click();
        await settle();
        expect(cards()).toHaveLength(1);
    });

    it("synchronizes external carrier replacements into the visible clip", async () => {
        await loadAuthoritativeArchitectureCatalog();
        start();
        const data = document.getElementById(
            "input_videostages",
        ) as HTMLTextAreaElement;
        data.value = JSON.stringify(payload(8));
        jest.advanceTimersByTime(250);
        expect(
            root().querySelector(".vst-editor-duration")?.textContent,
        ).toContain("8.0s");
    });

    it("keeps undo and redo usable across host parameter rebuilds", async () => {
        await loadAuthoritativeArchitectureCatalog();
        start();
        const clips = getState().clips;
        clips[0].duration = 8;
        saveClips(clips);
        required(app).init();
        document.dispatchEvent(
            new KeyboardEvent("keydown", {
                key: "z",
                ctrlKey: true,
                bubbles: true,
            }),
        );
        expect(getState().clips[0].duration).toBe(4);
        document.dispatchEvent(
            new KeyboardEvent("keydown", {
                key: "z",
                ctrlKey: true,
                shiftKey: true,
                bubbles: true,
            }),
        );
        expect(
            root().querySelector(".vst-editor-duration")?.textContent,
        ).toContain("8.0s");
    });

    it("flushes a pending prompt when the page is hidden", async () => {
        await loadAuthoritativeArchitectureCatalog();
        start();
        const prompt = required(
            root().querySelector<HTMLTextAreaElement>(
                '[data-section="prompt"] textarea',
            ),
        );
        prompt.focus();
        prompt.value = "Keep this draft";
        prompt.dispatchEvent(new Event("input", { bubbles: true }));
        window.dispatchEvent(new Event("pagehide"));
        expect(getState().clips[0].prompt).toBe("Keep this draft");
    });

    it("refreshes the editor after authoring settings change", async () => {
        await loadAuthoritativeArchitectureCatalog();
        const editor = createClipEditor();
        const refresh = jest.spyOn(editor, "refresh");
        app = createVideoStagesApplication(editor);
        app.init();
        window.dispatchEvent(new Event(TIMELINE_AUTHORING_SETTINGS_CHANGED));
        expect(refresh).toHaveBeenCalledTimes(1);
        expect(cards()).toHaveLength(1);
    });

    it("opens preview links in the production tab and exposes its enabled state", async () => {
        await loadAuthoritativeArchitectureCatalog();
        window.history.replaceState(
            null,
            "",
            "#VideoStages-Editor-Tab,Text2Image",
        );
        start();
        expect(window.location.hash).toBe(`#${EDITOR_TAB_ID},Text2Image`);
        const tab = required(
            document.querySelector<HTMLAnchorElement>(
                `a[href="#${EDITOR_TAB_ID}"]`,
            ),
        );
        expect(tab.title).toBe("Video Stages is enabled");
        expect(tab.querySelector(".vst-tab-check")?.textContent).toBe("✓");
        expect(tab.getAttribute("role")).toBe("tab");
        expect(tab.getAttribute("aria-selected")).toBe("true");
        expect(tab.tabIndex).toBe(0);
    });
    it("rebinds replacement host inputs and renders their carrier document", async () => {
        await loadAuthoritativeArchitectureCatalog();
        start();
        required(document.getElementById("input_prompt")).remove();
        const prompt = mountPromptBox("");
        required(app).init();
        (
            required(
                document.getElementById("input_videostages"),
            ) as HTMLTextAreaElement
        ).value = JSON.stringify(payload(12));
        prompt.dispatchEvent(new Event("change", { bubbles: true }));
        expect(
            root().querySelector(".vst-editor-duration")?.textContent,
        ).toContain("12.0s");
    });

    it("keeps the authored clip when surrounding global prompt prose changes", async () => {
        await loadAuthoritativeArchitectureCatalog();
        start();
        const prompt = required(
            document.getElementById("input_prompt"),
        ) as HTMLTextAreaElement;
        prompt.value = "A sunset over the harbor";
        prompt.dispatchEvent(new Event("input", { bubbles: true }));
        expect(cards()).toHaveLength(1);
        expect(
            root().querySelector(".vst-editor-duration")?.textContent,
        ).toContain("4.0s");
        expect(
            root().querySelector<HTMLTextAreaElement>(
                '[data-section="prompt"] textarea',
            )?.placeholder,
        ).toContain("A sunset over the harbor");
    });

    it.each([
        ["prompt-major", "prompt"],
        ["prompt-minor", "relay-prompts"],
    ] as const)("preserves %s caret through committed and explicit refreshes", async (kind, key) => {
        (
            required(
                document.getElementById("input_prompt"),
            ) as HTMLTextAreaElement
        ).value = "<videoclip[0]:1-3>old";
        await loadAuthoritativeArchitectureCatalog();
        start();
        setSelection(
            kind === "prompt-major"
                ? { kind, clipIdx: 0 }
                : { kind, clipIdx: 0, windowIdx: 0 },
        );
        const prompt = required(
            root().querySelector<HTMLTextAreaElement>(
                `[data-section="${key}"] textarea`,
            ),
        );
        const focusKey = prompt.dataset.vstFocusKey;
        prompt.focus();
        prompt.value = "Harbor sunset";
        prompt.setSelectionRange(6, 6);
        prompt.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(250);
        editor.refresh();
        const active = document.activeElement as HTMLTextAreaElement;
        expect(active.dataset.vstFocusKey).toBe(focusKey);
        expect(active.selectionStart).toBe(6);
        expect(active.value).toBe("Harbor sunset");
    });

    it("preserves the visible clip and form viewport across structural commits", async () => {
        await loadAuthoritativeArchitectureCatalog();
        start();
        required(
            root().querySelector<HTMLElement>(".vst-editor-sequence"),
        ).scrollTop = 140;
        required(
            root().querySelector<HTMLElement>(".vst-editor-scroll"),
        ).scrollTop = 240;
        const repaints = jest.spyOn(root(), "replaceChildren");
        const clips = getState().clips;
        clips.push({ ...structuredClone(clips[0]), id: undefined });
        saveClips(clips);
        expect(cards()).toHaveLength(2);
        expect(
            root().querySelector<HTMLElement>(".vst-editor-sequence")
                ?.scrollTop,
        ).toBe(140);
        expect(
            root().querySelector<HTMLElement>(".vst-editor-scroll")?.scrollTop,
        ).toBe(240);
        expect(repaints).toHaveBeenCalledTimes(1);
    });

    it("adds a selected clip with the preceding sampling settings and join", async () => {
        const data = {
            schemaVersion: 9,
            clips: [
                {
                    ...payload(2).clips[0],
                    boundaryOut: "continue",
                    boundaryOutCarryAudio: true,
                    boundaryOutOverlap: 16,
                    boundaryOutReferenceScale: 0.25,
                    boundaryOutReferenceIncludeSoundtrack: false,
                },
                {
                    ...payload(3).clips[0],
                    loras: [{ name: "look", weight: 0.6 }],
                    stages: [
                        {
                            model: "ltx-2.3.safetensors",
                            modelProfileId: "ltx-2.3",
                            sampler: "res_multistep",
                            scheduler: "sgm_uniform",
                            steps: 20,
                            cfgScale: 4,
                        },
                    ],
                },
            ],
        };
        (
            required(
                document.getElementById("input_videostages"),
            ) as HTMLTextAreaElement
        ).value = JSON.stringify(data);
        await loadAuthoritativeArchitectureCatalog();
        start();
        required(
            root().querySelector<HTMLButtonElement>('[data-testid="add-clip"]'),
        ).click();
        await settle();
        const clips = getState().clips;
        expect(clips).toHaveLength(3);
        expect(clips[1]).toMatchObject({
            boundaryOut: "continue",
            boundaryOutCarryAudio: true,
            boundaryOutOverlap: 16,
            boundaryOutReferenceScale: 0.25,
            boundaryOutReferenceIncludeSoundtrack: false,
        });
        expect(clips[2]).toMatchObject({
            duration: 3,
            stages: [
                {
                    loras: [{ name: "look", weight: 0.6 }],
                    model: "ltx-2.3.safetensors",
                    sampler: "res_multistep",
                    scheduler: "sgm_uniform",
                    steps: 20,
                    cfgScale: 4,
                },
            ],
        });
        expect(getSelection()).toEqual({
            kind: "prompt-major",
            clipIdx: 2,
        });
        expect(
            root().querySelector<HTMLElement>(".vst-editor-clip.selected")
                ?.dataset.clipIndex,
        ).toBe("2");
    });

    it("uses server-catalog models to create the first clip after delayed startup", async () => {
        required(document.getElementById("input_videomodel")).remove();
        const carrier = required(
            document.getElementById("input_videostages"),
        ) as HTMLTextAreaElement;
        carrier.value = JSON.stringify({ schemaVersion: 9, clips: [] });
        const original = carrier.value;
        const pending = deferred();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: () => pending.promise,
        });
        start();
        jest.advanceTimersByTime(250);
        expect(
            root()
                .querySelector('[data-catalog-status="loading"]')
                ?.getAttribute("aria-busy"),
        ).toBe("true");
        expect(carrier.value).toBe(original);
        pending.resolve(catalogWithModels("server-ltx.safetensors"));
        await settle();
        required(
            root().querySelector<HTMLButtonElement>(
                '[data-testid="add-first-clip"]',
            ),
        ).click();
        await settle();
        expect(getState().clips[0]).toMatchObject({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
            stages: [
                { model: "server-ltx.safetensors", modelProfileId: "ltx-2.3" },
            ],
        });
    });

    it("applies a queued host refresh after the initial catalog finishes", async () => {
        const initial = deferred();
        const trailing = deferred();
        const requestJson = jest
            .fn<() => Promise<unknown>>()
            .mockImplementationOnce(() => initial.promise)
            .mockImplementationOnce(() => trailing.promise);
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
        });
        start();
        await settle();
        for (const refresh of required(refreshParamsExtra)) refresh();
        initial.resolve(catalogWithModels("ltx-2.3.safetensors"));
        await settle();
        expect(
            root()
                .querySelector('[data-catalog-status="refreshing"]')
                ?.getAttribute("role"),
        ).toBe("status");
        expect(cards()).toHaveLength(1);
        trailing.resolve(
            catalogWithModels("ltx-2.3.safetensors", "updated-ltx.safetensors"),
        );
        await settle();
        expect(modelNames()).toContain("updated-ltx.safetensors");
    });

    it("keeps catalog-backed fields usable during refresh failure and accepts Retry", async () => {
        await loadAuthoritativeArchitectureCatalog();
        jest.spyOn(console, "warn").mockImplementation(() => undefined);
        const refresh = deferred();
        const retry = deferred();
        const requestJson = jest
            .fn<() => Promise<unknown>>()
            .mockImplementationOnce(() => refresh.promise)
            .mockImplementationOnce(() => retry.promise);
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
        });
        start();
        const carrier = required(
            document.getElementById("input_videostages"),
        ) as HTMLTextAreaElement;
        const original = carrier.value;
        for (const callback of required(refreshParamsExtra)) callback();
        await settle();
        refresh.reject(new Error("offline"));
        await settle();
        expect(
            root()
                .querySelector('[data-catalog-status="stale"]')
                ?.getAttribute("role"),
        ).toBe("alert");
        expect(modelNames()).toContain("ltx-2.3.safetensors");
        expect(cards()).toHaveLength(1);
        expect(carrier.value).toBe(original);
        required(
            root().querySelector<HTMLButtonElement>(".vst-catalog-retry"),
        ).click();
        retry.resolve(
            catalogWithModels(
                "ltx-2.3.safetensors",
                "replacement-ltx.safetensors",
            ),
        );
        await settle();
        expect(modelNames()).toContain("replacement-ltx.safetensors");
    });
});
