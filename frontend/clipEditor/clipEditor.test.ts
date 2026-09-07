import { afterEach, describe, expect, it, jest } from "@jest/globals";
import {
    authoringFixtureHarness,
    type ClipFixture,
} from "../__test_helpers__/authoringFixtures";
import { initVideoFixture } from "../__test_helpers__/clipFixtures";
import { mountCheckbox } from "../__test_helpers__/dom";
import { MEDIA_SOURCE_PREVIOUS_CLIP } from "../generatedMediaSource";
import { getVideoStagesHostBridge } from "../host";
import { getState, saveClips } from "../persistence/repository";
import { getSelection, setSelection } from "../selection";
import { createClipEditor } from "./clipEditor";
import { revealSelectedItem } from "./navigation";
import { EDITOR_BODY_ID, mountClipEditor } from "./tab";

const query = <T extends HTMLElement = HTMLElement>(selector: string): T => {
    const node = document.querySelector<T>(`#${EDITOR_BODY_ID} ${selector}`);
    if (!node) throw new Error(`Missing editor element: ${selector}`);
    return node;
};
const click = (id: string) =>
    query<HTMLButtonElement>(`[data-testid="${id}"]`).click();
const field = <T extends HTMLElement = HTMLInputElement>(
    section: string,
    label: string,
    selector = "input",
): T => {
    const row = Array.from(
        query(`[data-section="${section}"]`).querySelectorAll(".auto-input"),
    ).find(
        (node) =>
            node
                .querySelector(".auto-input-name")
                ?.firstChild?.textContent?.trim() === label,
    );
    const control = row?.querySelector<T>(selector);
    if (!control) throw new Error(`Missing ${section}/${label}/${selector}`);
    return control;
};
const change = (
    control: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
    value: string,
    event = "change",
) => {
    control.focus();
    control.value = value;
    control.dispatchEvent(new Event(event, { bubbles: true }));
    control.blur();
};

describe("clip editor", () => {
    let editor: ReturnType<typeof createClipEditor> | undefined;
    afterEach(() => {
        editor?.dispose();
        editor = undefined;
    });
    const harness = authoringFixtureHarness();
    const setup = (clips: ClipFixture[] = [{ duration: 4, stages: [{}] }]) => {
        harness.setupFixtures(clips, ["motion.safetensors"]);
        mountCheckbox("input_group_content_videostages_toggle", {
            checked: true,
        });
        document.body.insertAdjacentHTML(
            "beforeend",
            '<ul id="bottombartabcollection"><li><a href="#Tools-Tab">Tools</a></li></ul><div id="t2i_bottom_bar_content"></div>',
        );
        editor = createClipEditor();
        editor?.init();
    };

    it("mounts a single host tab before Tools on repeated initialization", () => {
        setup();
        editor?.init();
        expect(
            document.querySelectorAll('[data-testid="clip-editor-tab"]'),
        ).toHaveLength(1);
        expect(
            document.querySelector("#bottombartabcollection li:first-child a")
                ?.firstChild?.textContent,
        ).toBe("VideoStages");
        expect(mountClipEditor()).toBe(document.getElementById(EDITOR_BODY_ID));
    });
    it.each([
        ["boundary-ref", "references"],
        ["boundary", "join"],
    ] as const)("routes %s selections across empty clips to %s", (kind, destination) => {
        setup([
            { duration: 4, stages: [{}], boundaryOut: "continue" },
            { duration: 4, stages: [] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind, leftClipIdx: 0 });
        expect(query(".vst-editor-clip.selected").dataset.clipIndex).toBe("2");
        expect(
            query(`[data-section-link="${destination}"]`).getAttribute(
                "aria-current",
            ),
        ).toBe("location");
    });

    it("does not manufacture a host tab when SwarmUI's mount is absent", () => {
        expect(mountClipEditor()).toBeNull();
    });
    it("collapses the map without replacing drafts or changing the document", () => {
        setup();
        const prompt = query<HTMLTextAreaElement>(
            '[data-section="prompt"] textarea',
        );
        prompt.focus();
        prompt.value = "Still writing";
        const before = JSON.stringify(getState());
        click("toggle-map");
        expect(
            query('[data-testid="toggle-map"]').getAttribute("aria-expanded"),
        ).toBe("false");
        expect(
            document
                .getElementById(EDITOR_BODY_ID)
                ?.classList.contains("vst-editor-map-collapsed"),
        ).toBe(true);
        expect(query('[data-section="prompt"] textarea')).toBe(prompt);
        expect(prompt.value).toBe("Still writing");
        expect(JSON.stringify(getState())).toBe(before);
        editor?.refresh();
        expect(
            query('[data-testid="toggle-map"]').getAttribute("aria-expanded"),
        ).toBe("false");
        click("toggle-map");
        expect(
            document
                .getElementById(EDITOR_BODY_ID)
                ?.classList.contains("vst-editor-map-collapsed"),
        ).toBe(false);
    });
    it("preserves vertical map scroll when a prompt updates its card", () => {
        setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        query('[data-testid="clip-sequence"]').scrollTop = 140;
        change(
            query<HTMLTextAreaElement>('[data-section="prompt"] textarea'),
            "Live map draft",
            "input",
        );
        expect(query('[data-testid="clip-sequence"]').scrollTop).toBe(140);
        expect(
            query('[data-clip-index="0"] [data-testid="clip-prompt"]')
                .textContent,
        ).toContain("Live map draft");
    });
    it("deletes the chosen card while another clip is being edited", () => {
        setup(
            [0, 1, 2].map((index) => ({
                duration: 4,
                stages: [{}],
                prompt: `Clip ${index}`,
            })),
        );
        query('[data-clip-index="2"] [data-testid="delete-clip"]').click();
        expect(getState().clips.map((clip) => clip.prompt)).toEqual([
            "Clip 0",
            "Clip 1",
        ]);
        expect(query(".vst-editor-clip.selected").dataset.clipIndex).toBe("1");
    });
    it("keeps all sections mounted when the navigation changes", () => {
        setup();
        const prompt = query('[data-section="prompt"] textarea');
        click("nav-stages");
        click("nav-keyframes");
        expect(query('[data-section="prompt"] textarea')).toBe(prompt);
        expect(query('[data-section="stages"] select')).toBeTruthy();
        expect(
            query('[data-testid="nav-keyframes"]').getAttribute("aria-current"),
        ).toBe("location");
    });
    it("edits the authoritative prompt and its serialized carrier", () => {
        setup();
        change(
            query<HTMLTextAreaElement>('[data-section="prompt"] textarea'),
            "A rainy street",
            "input",
        );
        expect(getState().clips[0].prompt).toBe("A rainy street");
        expect(
            (document.getElementById("input_prompt") as HTMLTextAreaElement)
                .value,
        ).toContain("A rainy street");
    });
    it("uses the production model selector", () => {
        setup();
        change(
            field<HTMLSelectElement>("stages", "Model", "select"),
            "ltx-2.3-alt.safetensors",
        );
        expect(getState().clips[0].stages[0].model).toBe(
            "ltx-2.3-alt.safetensors",
        );
    });
    it("adds a stage through shared domain operations", () => {
        setup();
        click("add-stages");
        expect(getState().clips[0].stages).toHaveLength(2);
        expect(
            query('[data-section="stages"]').querySelectorAll(
                '[data-testid="editor-item"]',
            ),
        ).toHaveLength(2);
    });
    it("skips and deletes a later stage", () => {
        setup([{ duration: 4, stages: [{}, {}] }]);
        const toggle = () =>
            query<HTMLInputElement>('[data-testid="toggle-stage"]');
        expect(toggle().getAttribute("role")).toBe("switch");
        expect(toggle().checked).toBe(true);
        click("toggle-stage");
        expect(getState().clips[0].stages[1].skipped).toBe(true);
        expect(toggle().checked).toBe(false);
        expect(
            toggle()
                .closest('[data-testid="editor-item"]')
                ?.getAttribute("data-enabled"),
        ).toBe("false");
        expect(
            toggle().closest(".vst-editor-item-heading")?.textContent,
        ).toContain("Skipped");
        click("toggle-stage");
        expect(getState().clips[0].stages[1].skipped).toBe(false);
        expect(toggle().checked).toBe(true);
        query<HTMLButtonElement>(
            '[data-section="stages"] [data-testid="remove-item"]',
        ).click();
        expect(getState().clips[0].stages).toHaveLength(1);
    });
    it("protects the first stage and first clip from deletion", () => {
        setup();
        expect(
            query('[data-section="stages"]').querySelector(
                '[data-testid="remove-item"]',
            ),
        ).toBeNull();
        expect(
            query('[data-section="prompt"]').querySelector(
                '[data-testid="delete-clip"]',
            ),
        ).toBeNull();
    });
    it("keeps numeric drafts isolated across simultaneously mounted stages", () => {
        setup([{ duration: 4, stages: [{ steps: 8 }, { steps: 9 }] }]);
        const inputs = query(
            '[data-section="stages"]',
        ).querySelectorAll<HTMLInputElement>(
            'input[type="number"][data-field="steps"]',
        );
        expect(inputs).toHaveLength(2);
        change(inputs[0], "12", "input");
        change(
            query('[data-section="stages"]').querySelectorAll<HTMLInputElement>(
                'input[type="number"][data-field="steps"]',
            )[1],
            "16",
            "input",
        );
        expect(getState().clips[0].stages.map((stage) => stage.steps)).toEqual([
            12, 16,
        ]);
    });
    it("flushes a pending prompt before switching clips", () => {
        setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        const prompt = query<HTMLTextAreaElement>(
            '[data-section="prompt"] textarea',
        );
        prompt.focus();
        prompt.value = "First clip draft";
        prompt.dispatchEvent(new Event("input", { bubbles: true }));
        query<HTMLButtonElement>(
            '[data-clip-index="1"] [data-testid="clip-prompt"]',
        ).click();
        expect(getState().clips[0].prompt).toBe("First clip draft");
        expect(
            query<HTMLTextAreaElement>('[data-section="prompt"] textarea')
                .value,
        ).toBe("");
        expect(document.activeElement).toBe(
            query('[data-section="prompt"] textarea'),
        );
    });
    it("adds and selects a clip with an editable prompt", async () => {
        setup();
        click("add-clip");
        for (let i = 0; i < 12; i++) await Promise.resolve();
        expect(getState().clips).toHaveLength(2);
        expect(query(".vst-editor-clip.selected").dataset.clipIndex).toBe("1");
        expect(document.activeElement).toBe(
            query('[data-section="prompt"] textarea'),
        );
    });
    it("skips and deletes a later clip and reconciles the selection", () => {
        setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        query<HTMLButtonElement>(
            '[data-clip-index="1"] [data-testid="select-clip"]',
        ).click();
        click("toggle-clip");
        expect(getState().clips[1].skipped).toBe(true);
        click("delete-clip");
        expect(getState().clips).toHaveLength(1);
        expect(query(".vst-editor-clip.selected").dataset.clipIndex).toBe("0");
    });
    it("keeps scroll position after a structural stage action", () => {
        setup([{ duration: 4, stages: [{}, {}] }]);
        query('[data-testid="clip-sections"]').scrollTop = 120;
        click("toggle-stage");
        expect(query('[data-testid="clip-sections"]').scrollTop).toBe(120);
    });
    it("selects the immediate left neighbor after deleting a clip", () => {
        setup(
            [0, 1, 2, 3].map((index) => ({
                duration: 4,
                stages: [{}],
                prompt: `Prompt ${index}`,
            })),
        );
        query('[data-clip-index="2"]').click();
        query('[data-clip-index="2"] [data-testid="delete-clip"]').click();
        expect(getState().clips.map((clip) => clip.prompt)).toEqual([
            "Prompt 0",
            "Prompt 1",
            "Prompt 3",
        ]);
        expect(query(".vst-editor-clip.selected").dataset.clipIndex).toBe("1");
        expect(
            query<HTMLTextAreaElement>('[data-section="prompt"] textarea')
                .value,
        ).toBe("Prompt 1");
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 1,
            stageIdx: 0,
        });
    });
    it.each([
        "",
        " [data-testid=clip-prompt]",
        " [data-testid=clip-stages]",
        " [data-testid=select-clip]",
    ])("Shift-click deletes a clip through its card or shortcut (%s)", (target) => {
        setup(
            [0, 1, 2].map((index) => ({
                duration: 4,
                stages: [{}],
                prompt: `Prompt ${index}`,
            })),
        );
        const cardTarget = query(`[data-clip-index="2"]${target}`);
        const press = new MouseEvent("mousedown", {
            bubbles: true,
            cancelable: true,
            shiftKey: true,
        });
        cardTarget.dispatchEvent(press);
        expect(press.defaultPrevented).toBe(true);
        cardTarget.dispatchEvent(
            new MouseEvent("click", { bubbles: true, shiftKey: true }),
        );
        expect(getState().clips).toHaveLength(2);
        expect(query(".vst-editor-clip.selected").dataset.clipIndex).toBe("1");
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 1,
            stageIdx: 0,
        });
    });
    it.each([
        "audio",
        "video",
        "audio video",
    ])("identifies %s sources and clears the highlight when the consumer changes", (usage) => {
        setup([
            {
                duration: 4,
                stages: [{}],
                audioOnly: usage === "audio",
                boundaryOut: "continue",
            },
            {
                duration: 4,
                stages: [{}],
                audioSource: usage.includes("audio")
                    ? MEDIA_SOURCE_PREVIOUS_CLIP
                    : "Native",
                initVideo: usage.includes("video")
                    ? initVideoFixture({ source: MEDIA_SOURCE_PREVIOUS_CLIP })
                    : undefined,
            },
        ]);
        const expectedUsage = usage.includes("video") ? "video" : "audio";
        const source = query('[data-clip-index="0"]');
        expect(source.dataset.sourceUsage).toBe(expectedUsage);
        expect(source.title).toContain("Clip 1");
        expect(
            source.querySelector('[data-testid="select-clip"]')?.textContent,
        ).toBe("0");
        expect(source.textContent).toContain(`${expectedUsage} source`);
        expect(query('[data-testid="edit-join"]').dataset.sourceUsage).toBe(
            expectedUsage,
        );
        expect(query('[data-testid="edit-join"]').textContent).toBe(
            `↓ Source ${expectedUsage} ↓ Feeds next`,
        );
        expect(query('[data-clip-index="1"]').dataset.sourceUsage).toBe("");
        const clips = getState().clips;
        clips[0].audioOnly = false;
        clips[1].audioSource = "Native";
        clips[1].initVideo = null;
        saveClips(clips);
        expect(query('[data-clip-index="0"]').dataset.sourceUsage).toBe("");
        expect(query('[data-testid="edit-join"]').dataset.sourceUsage).toBe("");
        expect(query('[data-testid="edit-join"]').textContent).toBe(
            "↓ continue ↓",
        );
    });
    it("switches consumption through the join selector and restores normal transitions", () => {
        setup([
            { duration: 4, stages: [{}], boundaryOut: "continue" },
            { duration: 4, stages: [{}] },
        ]);
        click("edit-join");
        const selectJoin = (value: string) =>
            change(field<HTMLSelectElement>("join", "Join", "select"), value);
        selectJoin("source-audio");
        expect(getState().clips[0].audioOnly).toBe(true);
        expect(query('[data-testid="edit-join"]').textContent).toBe(
            "↓ Source audio ↓ Feeds next",
        );
        expect(query('[data-testid="output-duration"]').textContent).toBe(
            "4.0s output",
        );
        selectJoin("source-video");
        expect(getState().clips[0].audioOnly).toBe(false);
        expect(getState().clips[1].initVideo?.source).toBe(
            MEDIA_SOURCE_PREVIOUS_CLIP,
        );
        expect(query('[data-testid="edit-join"]').textContent).toBe(
            "↓ Source video ↓ Feeds next",
        );
        expect(query('[data-testid="output-duration"]').textContent).toBe(
            "4.0s output",
        );
        selectJoin("cut");
        expect(getState().clips[1].initVideo).toBeNull();
        expect(query('[data-testid="edit-join"]').dataset.sourceUsage).toBe("");
        expect(query('[data-testid="edit-join"]').textContent).toBe("↓ cut ↓");
        expect(query('[data-testid="output-duration"]').textContent).toBe(
            "8.1s output",
        );
    });
    it("follows a keyframe selected in the production timeline", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                frameRefs: [{ source: "Base Output", frame: 1 }],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        expect(
            query('[data-testid="nav-keyframes"]').getAttribute("aria-current"),
        ).toBe("location");
    });
    it("publishes field ownership to the shared selection", () => {
        setup([{ duration: 4, stages: [{}, {}] }]);
        query('[data-section="stages"]')
            .querySelectorAll<HTMLSelectElement>("select")[3]
            ?.focus();
        const second = query(
            '[data-section="stages"]',
        ).querySelectorAll<HTMLElement>('[data-testid="editor-item"]')[1];
        second.querySelector<HTMLSelectElement>("select")?.focus();
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 0,
            stageIdx: 1,
        });
    });
    it("refreshes values changed through the production document", () => {
        setup();
        const clips = getState().clips;
        clips[0].prompt = "Changed in timeline";
        saveClips(clips);
        expect(
            query<HTMLTextAreaElement>('[data-section="prompt"] textarea')
                .value,
        ).toBe("Changed in timeline");
    });
    it("edits the previous clip's boundary from the join shortcut", () => {
        setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        click("edit-join");
        expect(
            query('[data-testid="nav-join"]').getAttribute("aria-current"),
        ).toBe("location");
        expect(
            field<HTMLSelectElement>("join", "Join", "select").dataset.testid,
        ).toBe("field-join-select");
        change(field<HTMLSelectElement>("join", "Join", "select"), "crossfade");
        expect(getState().clips[0].boundaryOut).toBe("crossfade");
    });
    it("edits global output dimensions using the production settings panel", () => {
        setup();
        click("output-settings");
        change(
            field<HTMLSelectElement>("output", "Aspect Ratio", "select"),
            "16:9",
        );
        expect(getState()).toMatchObject({
            dimsExplicit: true,
            width: 1344,
            height: 768,
        });
        expect(query('[data-testid="output-settings"]').textContent).toContain(
            "1344×768",
        );
    });
    it("adds and removes a keyframe without hiding other sections", () => {
        setup();
        click("add-keyframes");
        expect(getState().clips[0].frameRefs).toHaveLength(1);
        query<HTMLButtonElement>(
            '[data-section="keyframes"] [data-testid="remove-item"]',
        ).click();
        expect(getState().clips[0].frameRefs).toHaveLength(0);
        expect(query('[data-section="stages"] select')).toBeTruthy();
    });
    it("adds and removes a reference", () => {
        setup();
        click("add-references");
        expect(getState().clips[0].references).toHaveLength(1);
        query<HTMLButtonElement>(
            '[data-section="references"] [data-testid="remove-item"]',
        ).click();
        expect(getState().clips[0].references).toHaveLength(0);
    });
    it("adds and removes a clip LoRA", () => {
        setup();
        click("add-loras");
        expect(getState().clips[0].loras).toHaveLength(1);
        query<HTMLButtonElement>(
            '[data-section="loras"] [data-testid="remove-item"]',
        ).click();
        expect(getState().clips[0].loras).toHaveLength(0);
    });
    it("adds and removes an IC-LoRA", () => {
        setup();
        click("add-ic-loras");
        expect(getState().clips[0].icLoras).toHaveLength(1);
        query<HTMLButtonElement>(
            '[data-section="ic-loras"] [data-testid="remove-item"]',
        ).click();
        expect(getState().clips[0].icLoras).toHaveLength(0);
    });
    it("adds and removes a relay prompt", () => {
        setup();
        click("add-relay-prompts");
        expect(getState().clips[0].promptWindows).toHaveLength(1);
        query<HTMLButtonElement>(
            '[data-section="relay-prompts"] [data-testid="remove-item"]',
        ).click();
        expect(getState().clips[0].promptWindows).toHaveLength(0);
    });
    it("reflects enabled state while leaving controls editable", () => {
        setup();
        click("toggle-enabled");
        expect(
            query('[data-testid="toggle-enabled"]').getAttribute(
                "aria-checked",
            ),
        ).toBe("false");
        expect(
            query<HTMLTextAreaElement>('[data-section="prompt"] textarea')
                .disabled,
        ).toBe(false);
    });
    it("provides production audio, source video, retake and extension settings", () => {
        setup();
        expect(
            field<HTMLSelectElement>("audio", "Audio Source", "select"),
        ).toBeTruthy();
        expect(query('[data-section="source-video"]').textContent).toContain(
            "Video",
        );
        expect(query('[data-section="settings"]').textContent).toContain(
            "Make Them Bonita",
        );
        expect(query('[data-section="settings"]').textContent).toContain(
            "Reset VideoStages",
        );
    });
    it("keeps control selectors and focus keys unique within repeated items", () => {
        setup([{ duration: 4, stages: [{}, {}] }]);
        const items = query(
            '[data-section="stages"]',
        ).querySelectorAll<HTMLElement>('[data-testid="editor-item"]');
        for (const item of items) {
            const ids = Array.from(
                item.querySelectorAll<HTMLElement>(
                    "input,select,textarea,button",
                ),
            ).map((node) => node.dataset.testid);
            expect(new Set(ids).size).toBe(ids.length);
            expect(ids.every(Boolean)).toBe(true);
        }
        const keys = Array.from(
            query('[data-section="stages"]').querySelectorAll<HTMLElement>(
                "[data-vst-focus-key]",
            ),
        ).map((node) => node.dataset.vstFocusKey);
        expect(new Set(keys).size).toBe(keys.length);
    });
    it("unsubscribes on disposal", () => {
        setup();
        const prompt = query('[data-section="prompt"] textarea');
        editor?.dispose();
        const clips = getState().clips;
        clips[0].prompt = "After disposal";
        saveClips(clips);
        expect(query('[data-section="prompt"] textarea')).toBe(prompt);
        expect((prompt as HTMLTextAreaElement).value).toBe("");
    });
    it("does not restore a removed clip's stale navigation", () => {
        setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        click("edit-join");
        query('[data-testid="clip-sections"]').scrollTop = 480;
        saveClips([getState().clips[0]]);
        expect(
            query('[data-testid="nav-prompt"]').getAttribute("aria-current"),
        ).toBe("location");
        expect(query('[data-testid="clip-sections"]').scrollTop).toBe(0);
    });
    it("marks the visible section after manual scrolling", () => {
        setup();
        const pane = query('[data-testid="clip-sections"]');
        jest.spyOn(pane, "getBoundingClientRect").mockReturnValue({
            top: 100,
        } as DOMRect);
        const sections = pane.querySelectorAll<HTMLElement>("[data-section]");
        sections.forEach((node, index) => {
            jest.spyOn(node, "getBoundingClientRect").mockReturnValue({
                top: index === 0 ? -100 : index === 1 ? 100 : 500 + index * 100,
            } as DOMRect);
        });
        pane.dispatchEvent(new Event("scroll"));
        expect(
            query('[data-testid="nav-stages"]').getAttribute("aria-current"),
        ).toBe("location");
    });
    it.each([
        ["stages", "S0", "S1"],
        ["keyframes", "", ""],
    ])("updates the sticky %s heading while scrolling", (key, first, second) => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                frameRefs: [
                    { source: "Base Output", frame: 1 },
                    { source: "Base Output", frame: 2 },
                ],
            },
        ]);
        const pane = query('[data-testid="clip-sections"]');
        jest.spyOn(pane, "getBoundingClientRect").mockReturnValue({
            top: 100,
        } as DOMRect);
        const panel = query(`[data-section="${key}"]`);
        const header = panel.querySelector<HTMLElement>(
            ".vst-editor-section-heading",
        );
        if (!header) throw new Error("Missing section heading");
        jest.spyOn(header, "getBoundingClientRect").mockReturnValue({
            top: 100,
            bottom: 158,
        } as DOMRect);
        const items = panel.querySelectorAll<HTMLElement>(
            '[data-testid="editor-item"]',
        );
        const firstBottom = jest
            .spyOn(items[0], "getBoundingClientRect")
            .mockReturnValue({ bottom: 400 } as DOMRect);
        jest.spyOn(items[1], "getBoundingClientRect").mockReturnValue({
            bottom: 800,
        } as DOMRect);
        const firstHeading = items[0].querySelector("h4");
        const secondHeading = items[1].querySelector("h4");
        if (!firstHeading || !secondHeading)
            throw new Error("Missing item heading");
        const firstTitle = jest
            .spyOn(firstHeading, "getBoundingClientRect")
            .mockReturnValue({ bottom: 200 } as DOMRect);
        const secondTitle = jest
            .spyOn(secondHeading, "getBoundingClientRect")
            .mockReturnValue({ bottom: 450 } as DOMRect);
        const label = panel.querySelector<HTMLElement>(
            '[data-testid="sticky-item-label"]',
        );
        if (!label) throw new Error("Missing sticky item label");
        pane.dispatchEvent(new Event("scroll"));
        expect(label.hidden).toBe(true);
        firstTitle.mockReturnValue({ bottom: 150 } as DOMRect);
        pane.dispatchEvent(new Event("scroll"));
        expect(label.hidden).toBe(!first);
        expect(label.textContent).toBe(first ? `· ${first}` : "");
        firstBottom.mockReturnValue({ bottom: 145 } as DOMRect);
        secondTitle.mockReturnValue({ bottom: 150 } as DOMRect);
        pane.dispatchEvent(new Event("scroll"));
        expect(label.textContent).toBe(second ? `· ${second}` : "");
        firstBottom.mockReturnValue({ bottom: 400 } as DOMRect);
        secondTitle.mockReturnValue({ bottom: 450 } as DOMRect);
        pane.dispatchEvent(new Event("scroll"));
        expect(label.textContent).toBe(first ? `· ${first}` : "");
        firstTitle.mockReturnValue({ bottom: 200 } as DOMRect);
        pane.dispatchEvent(new Event("scroll"));
        expect(label.hidden).toBe(true);
    });
    it("jumps within the editor rather than scrolling the surrounding app", () => {
        setup();
        const pane = query('[data-testid="clip-sections"]');
        jest.spyOn(pane, "getBoundingClientRect").mockReturnValue({
            top: 100,
        } as DOMRect);
        jest.spyOn(
            query('[data-section="stages"]'),
            "getBoundingClientRect",
        ).mockReturnValue({ top: 500 } as DOMRect);
        click("nav-stages");
        expect(pane.scrollTop).toBe(400);
        expect(document.documentElement.scrollTop).toBe(0);
    });
    it("exposes a usable empty state", async () => {
        setup([]);
        click("add-first-clip");
        for (let i = 0; i < 12; i++) await Promise.resolve();
        expect(getState().clips).toHaveLength(1);
        expect(query('[data-section="prompt"] textarea')).toBeTruthy();
    });
    it("provides the full prompt through a native tooltip on its compact shortcut", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                prompt: "A long prompt with important detail",
            },
        ]);
        expect(query('[data-testid="clip-prompt"]').title).toBe(
            "A long prompt with important detail",
        );
        click("clip-prompt");
        expect(document.activeElement).toBe(
            query('[data-section="prompt"] textarea'),
        );
    });
    it("retains the current section when selecting another clip", () => {
        setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        click("nav-keyframes");
        query<HTMLButtonElement>(
            '[data-clip-index="1"] [data-testid="select-clip"]',
        ).click();
        expect(
            query('[data-testid="nav-keyframes"]').getAttribute("aria-current"),
        ).toBe("location");
    });
    it("deletes a collapsed clip through the host action menu", () => {
        const menus: { key: string; action(): void }[][] = [];
        Object.defineProperty(globalThis, "AdvancedPopover", {
            configurable: true,
            value: class {
                constructor(
                    _id: string,
                    options: { key: string; action(): void }[],
                ) {
                    menus.push(options);
                }
            },
        });
        try {
            setup([
                { duration: 4, stages: [{}] },
                { duration: 5, stages: [{}] },
            ]);
            click("toggle-map");
            query('[data-clip-index="1"] [data-testid="clip-actions"]').click();
            expect(menus[0][0].key).toBe("Delete Clip 1");
            menus[0][0].action();
            expect(getState().clips).toHaveLength(1);
            expect(query('[data-clip-index="0"]').classList).toContain(
                "selected",
            );
        } finally {
            Reflect.deleteProperty(globalThis, "AdvancedPopover");
        }
    });
    it("preserves an unsupported persisted retake for repair", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                retake: { startSeconds: 0, lengthSeconds: 1, strength: 0.5 },
            },
        ]);
        expect(query('[data-section="retake"]').textContent).toContain(
            "Retake",
        );
    });
    it("keeps section actions out of SwarmUI's delegated group toggler", () => {
        setup();
        const toggle = jest.fn((event: Event) => {
            if ((event.target as HTMLElement).closest(".input-group-header"))
                throw new Error("Host toggled a continuous section");
        });
        document.addEventListener("click", toggle);
        try {
            click("add-keyframes");
            query('[data-section="stages"] h3').click();
            expect(toggle).not.toHaveBeenCalled();
        } finally {
            document.removeEventListener("click", toggle);
        }
    });
    it("selects a clip through its padding and duration without overriding prompt shortcuts", () => {
        setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        click("nav-keyframes");
        query('[data-clip-index="1"]').click();
        expect(query(".vst-editor-clip.selected").dataset.clipIndex).toBe("1");
        query('[data-clip-index="0"] .vst-editor-duration').click();
        expect(query(".vst-editor-clip.selected").dataset.clipIndex).toBe("0");
        query<HTMLButtonElement>(
            '[data-clip-index="1"] [data-testid="clip-prompt"]',
        ).click();
        expect(
            query('[data-testid="nav-prompt"]').getAttribute("aria-current"),
        ).toBe("location");
        expect(document.activeElement).toBe(
            query('[data-section="prompt"] textarea'),
        );
    });
    it("shows output length from the production timing plan and excludes skipped clips", () => {
        setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        expect(query('[data-testid="output-duration"]').textContent).toBe(
            "8.1s output",
        );
        query('[data-clip-index="1"]').click();
        click("toggle-clip");
        expect(query('[data-testid="output-duration"]').textContent).toBe(
            "4.0s output",
        );
    });
    it("keeps the native enable switch outside the scrolling clip sequence", () => {
        setup();
        const toggle = query<HTMLInputElement>(
            '[data-testid="toggle-enabled"]',
        );
        expect(toggle.type).toBe("checkbox");
        expect(toggle.classList.contains("auto-slider-toggle")).toBe(true);
        expect(toggle.closest('[data-testid="clip-sequence"]')).toBeNull();
        click("toggle-enabled");
        expect(query('[data-testid="clip-strip"]').dataset.enabled).toBe(
            "false",
        );
        expect(query(".vst-editor-status strong").textContent).toBe("Disabled");
        expect(query("#vst-editor-disabled-description").textContent).toContain(
            "switch above",
        );
    });
    it("toggles the enabled state through its visible label", () => {
        setup();
        query(".vst-editor-status strong").click();
        expect(query('[data-testid="clip-strip"]').dataset.enabled).toBe(
            "false",
        );
        query(".vst-editor-status strong").click();
        expect(query('[data-testid="clip-strip"]').dataset.enabled).toBe(
            "true",
        );
    });
    it("keeps sidebar and form order aligned with LoRAs after stages and audio after source video", () => {
        setup();
        const sections = [
            ...query(
                '[data-testid="clip-sections"]',
            ).querySelectorAll<HTMLElement>(":scope > [data-section]"),
        ].map((node) => node.dataset.section);
        const links = [
            ...query(
                '[data-testid="section-navigation"]',
            ).querySelectorAll<HTMLElement>("[data-section-link]"),
        ].map((node) => node.dataset.sectionLink);
        expect(links).toEqual(sections);
        expect(sections[sections.indexOf("stages") + 1]).toBe("loras");
        expect(sections[sections.indexOf("source-video") + 1]).toBe("audio");
    });
    it("composes prompt and clip controls into the same field grid", () => {
        setup();
        const prompt = query('[data-section="prompt"] textarea').closest(
            ".vst-form-field",
        );
        const duration = query('[data-section="prompt"] .vst-stage-slider');
        expect(prompt?.parentElement).toBe(duration.parentElement);
        expect(duration.parentElement?.classList).toContain(
            "vst-editor-fields",
        );
        change(field("prompt", "Duration (s)", 'input[type="number"]'), "7");
        expect(getState().clips[0].duration).toBe(7);
    });
    it("focuses and selects the numeric value when a slider label is clicked", () => {
        setup();
        const slider = query('[data-section="prompt"] .vst-stage-slider');
        const number = slider.querySelector<HTMLInputElement>(
            'input[type="number"]',
        );
        if (!number) throw new Error("Missing slider number input");
        const select = jest.spyOn(number, "select");
        slider.querySelector("label")?.click();
        expect(document.activeElement).toBe(number);
        expect(select).toHaveBeenCalledTimes(1);
    });
    it("places Make Them Bonita after stage reference strengths", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                frameRefs: [{ source: "Base Output", frame: 1 }],
            },
        ]);
        jest.spyOn(
            getVideoStagesHostBridge(),
            "hasBackendFeature",
        ).mockReturnValue(true);
        editor?.refresh();
        const stages = query(
            '[data-section="stages"]',
        ).querySelectorAll<HTMLElement>(".vst-detail-params");
        expect(stages[1].querySelector(".vst-stage-ref-slider")).not.toBeNull();
        expect(stages[1].lastElementChild?.classList).toContain(
            "vst-character-refine-section",
        );
        const toggle = () =>
            query<HTMLInputElement>(
                '.vst-character-refine-section input[type="checkbox"]',
            );
        toggle().click();
        const strength = () =>
            query<HTMLInputElement>(
                '[data-section="stages"] [data-testid="editor-item"]:last-child .vst-stage-ref-slider input[type="number"]',
            );
        expect(strength().disabled).toBe(true);
        const lastStage = query('[data-section="stages"]').querySelectorAll(
            ".vst-detail-params",
        )[1];
        expect(lastStage.lastElementChild?.classList).toContain(
            "vst-character-refine-section",
        );
        toggle().click();
        expect(strength().disabled).toBe(false);
        change(strength(), "0.6");
        click("nav-prompt");
        expect(getState().clips[0].stages[1].frameRefStrengths).toEqual([0.6]);
    });
    it("derives media styling from edits and external document changes for a standalone clip", () => {
        setup();
        const card = () => query('[data-testid="clip-card"]');
        click("field-audio-only-checkbox");
        expect(getState().clips[0].audioOnly).toBe(true);
        expect(card().dataset.mediaKind).toBe("audio");
        expect(card().dataset.sourceUsage).toBe("");
        expect(card().textContent).toContain("audio only");
        const clips = getState().clips;
        clips[0].audioOnly = false;
        saveClips(clips);
        expect(card().dataset.mediaKind).toBe("video");
        expect(card().textContent).not.toContain("audio only");
    });
    it("keeps common sampling controls ahead of conditional refinement controls", () => {
        setup([{ duration: 4, stages: [{}, {}] }]);
        const rows = query(
            '[data-section="stages"]',
        ).querySelectorAll<HTMLElement>('[data-testid="editor-item"]');
        const controls = (row: HTMLElement) =>
            Array.from(
                row.querySelectorAll<HTMLElement>(
                    ".vst-detail-params > .auto-input, .vst-detail-params > .vst-stage-slider",
                ),
            ).map(
                (node) => node.querySelector(".auto-input-name")?.textContent,
            );
        expect(controls(rows[0]).slice(0, 5)).toEqual([
            "Model",
            "Steps",
            "CFG Scale",
            "Sampler",
            "Scheduler",
        ]);
        expect(controls(rows[1]).slice(0, 8)).toEqual([
            "Model",
            "Steps",
            "CFG Scale",
            "Sampler",
            "Scheduler",
            "Control",
            "Upscale",
            "Upscale Method",
        ]);
    });
    it("keeps keyframe position before optional upload previews", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                frameRefs: [{ source: "Upload", frame: 1 }],
            },
        ]);
        const fields = query(".vst-detail-ref-editor");
        expect(
            Array.from(fields.children)
                .slice(0, 3)
                .every((node) => node.classList.contains("auto-input")),
        ).toBe(true);
        expect(fields.children[1].textContent).toContain("Attach at Frame");
        expect(fields.children[2].textContent).toContain("Count from clip end");
    });
    it("reveals a selected repeated item below its sticky section heading", () => {
        setup([{ duration: 4, stages: [{}, {}] }]);
        const pane = query('[data-testid="clip-sections"]');
        const item = query(
            '[data-section="stages"]',
        ).querySelectorAll<HTMLElement>('[data-testid="editor-item"]')[1];
        jest.spyOn(pane, "getBoundingClientRect").mockReturnValue({
            top: 100,
            bottom: 500,
        } as DOMRect);
        jest.spyOn(item, "getBoundingClientRect").mockReturnValue({
            top: 700,
            bottom: 900,
        } as DOMRect);
        jest.spyOn(
            query('[data-section="stages"] .vst-editor-section-heading'),
            "getBoundingClientRect",
        ).mockReturnValue({ height: 42 } as DOMRect);
        revealSelectedItem(pane, { kind: "clip", clipIdx: 0, stageIdx: 1 });
        expect(pane.scrollTop).toBe(558);
    });
});
