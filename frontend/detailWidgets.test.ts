import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import {
    resetBrowserMediaStoreForTests,
    resolveBrowserMedia,
} from "./browserMediaStore";
import {
    buildAccordionSection,
    buildCheckbox,
    buildField,
    buildMediaPickRow,
    buildTextarea,
    resetRememberedAccordionSections,
    setFieldDisabled,
} from "./detailWidgets";
import { setVideoStagesHostBridgeForTests } from "./host";
import { createDefaultVideoStagesHostBridge } from "./host/defaultVideoStagesHostBridge";

type MutableGlobal = typeof globalThis & {
    doPopover?: (id: string, e?: Event) => void;
    inputBrowserHelper?: unknown;
};

const glob = globalThis as MutableGlobal;

describe("textarea layout", () => {
    it("applies reusable initial and maximum row sizing", () => {
        const editor = buildTextarea("", "Prompt", "prompt", jest.fn(), {
            rows: 3,
            maxRows: 8,
        });

        expect(editor.rows).toBe(3);
        expect(editor.classList).toContain("vst-textarea-limited");
        expect(editor.style.getPropertyValue("--vst-textarea-max-lines")).toBe(
            "8lh",
        );
    });
});

describe("buildField help popovers", () => {
    it("uses the shared field label to activate a checkbox exactly once", () => {
        const changed = jest.fn();
        const row = buildCheckbox("Audio Only", false, changed);
        document.body.append(row);
        row.querySelector("label")?.click();
        expect(row.querySelector("input")?.checked).toBe(true);
        expect(changed).toHaveBeenCalledTimes(1);
        expect(changed).toHaveBeenCalledWith(true);
        row.remove();
    });
    afterEach(() => {
        Reflect.deleteProperty(glob, "doPopover");
    });

    it("renders no ? button or popover when no help text is given", () => {
        const field = buildField("Steps", document.createElement("select"));
        expect(field.querySelector(".info-popover-button")).toBeNull();
        expect(field.querySelector(".sui-popover")).toBeNull();
        expect(field.querySelector(".auto-input-name")?.textContent).toBe(
            "Steps",
        );
    });

    it("renders a ? button and a popover with matching ids", () => {
        const field = buildField(
            "Control",
            document.createElement("select"),
            undefined,
            "Regeneration strength.",
        );
        const button = field.querySelector<HTMLElement>(
            ".auto-input-qbutton.info-popover-button",
        );
        const popover = field.querySelector<HTMLElement>(
            ".sui-popover.sui-info-popover",
        );
        expect(button).not.toBeNull();
        expect(button?.textContent).toBe("?");
        expect(popover).not.toBeNull();
        expect(popover?.id).toMatch(/^popover_vst_control_\d+$/);
        expect(field.querySelector(".auto-input-name")?.textContent).toBe(
            "Control",
        );
        expect(popover?.querySelector("b")?.textContent).toBe("Control");
        expect(popover?.textContent).toContain("Regeneration strength.");
        expect(button?.parentElement?.firstElementChild).toBe(button);
        expect(
            button?.nextElementSibling?.classList.contains("auto-input-name"),
        ).toBe(true);
    });

    it("routes the button click to the host doPopover with the popover's id", () => {
        const calls: string[] = [];
        glob.doPopover = (id: string) => {
            calls.push(id);
        };
        const field = buildField(
            "Upscale",
            document.createElement("select"),
            undefined,
            "Resolution multiplier.",
        );
        const button = field.querySelector<HTMLElement>(".info-popover-button");
        const popover = field.querySelector<HTMLElement>(".sui-popover");
        button?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(calls).toHaveLength(1);
        expect(`popover_${calls[0]}`).toBe(popover?.id);
    });

    it("does not throw when doPopover is unavailable", () => {
        const field = buildField(
            "Steps",
            document.createElement("select"),
            undefined,
            "How many denoising steps.",
        );
        const button = field.querySelector<HTMLElement>(".info-popover-button");
        expect(() =>
            button?.dispatchEvent(new MouseEvent("click", { bubbles: true })),
        ).not.toThrow();
    });

    it("attaches help to a checkbox label", () => {
        const check = buildCheckbox("Reuse Audio", false, () => {}, {
            help: "Carry the previous clip's audio.",
        });
        expect(check.tagName).toBe("DIV");
        expect(check.children[0]?.tagName).toBe("LABEL");
        expect(check.children[1]?.classList.contains("auto-checkbox")).toBe(
            true,
        );
        expect(
            check.querySelector<HTMLInputElement>(".auto-checkbox")?.dataset
                .name,
        ).toBe("Reuse Audio");
        expect(check.querySelector(".info-popover-button")).not.toBeNull();
        expect(check.querySelector(".sui-popover")?.id).toMatch(
            /^popover_vst_reuse-audio_\d+$/,
        );
        expect(
            check.querySelector(".auto-input-name")?.firstChild?.textContent,
        ).toBe("Reuse Audio");
        expect(
            check.querySelector("label")?.firstElementChild?.textContent,
        ).toBe("?");
        expect(
            check.querySelector("label")?.firstElementChild?.classList,
        ).toContain("info-popover-button");
        expect(
            check
                .querySelector("label")
                ?.lastElementChild?.classList.contains("auto-input-name"),
        ).toBe(true);
    });
});

describe("buildMediaPickRow", () => {
    it("opens a saved image from the thumbnail and restores focus on close", () => {
        const previous = {
            setMediaFileDirect: globalThis.setMediaFileDirect,
            getMediaType: globalThis.getMediaType,
        };
        Object.assign(globalThis, {
            getMediaType: () => "image",
            setMediaFileDirect: (input: HTMLInputElement, src: string) => {
                const preview = input
                    .closest(".auto-input")
                    ?.querySelector(".auto-input-preview");
                const image = document.createElement("img");
                image.src = src;
                preview?.append(image);
            },
        });
        try {
            const row = buildMediaPickRow(
                "Image",
                "image/*",
                ["image"],
                "sample.png",
                jest.fn(),
                jest.fn(),
                "data:image/png;base64,AAAA",
            );
            document.body.append(row);
            const thumbnail = row.querySelector<HTMLButtonElement>(
                '[data-testid="media-preview"]',
            );
            expect(thumbnail).not.toBeNull();
            thumbnail?.focus();
            thumbnail?.click();
            expect(
                document
                    .querySelector(".vst-media-preview-modal img")
                    ?.getAttribute("src"),
            ).toBe("data:image/png;base64,AAAA");
            document.dispatchEvent(
                new KeyboardEvent("keydown", { key: "Escape" }),
            );
            expect(
                document.querySelector(".vst-media-preview-modal"),
            ).toBeNull();
            expect(document.activeElement).toBe(thumbnail);
            row.remove();
        } finally {
            Object.assign(globalThis, previous);
        }
    });
    afterEach(() => {
        resetBrowserMediaStoreForTests();
        setVideoStagesHostBridgeForTests(null);
        // Restore the host browser seeded by the test setup.
        if (savedBrowser !== undefined) {
            glob.inputBrowserHelper = savedBrowser;
        }
    });

    let savedBrowser: unknown;

    it("offers a Select button when inputBrowserHelper exists", () => {
        savedBrowser = glob.inputBrowserHelper;
        glob.inputBrowserHelper = {
            openInputBrowser: () => {},
        };
        const row = buildMediaPickRow(
            "Audio Upload",
            "audio/*",
            ["audio"],
            null,
            () => {},
            () => {},
        );
        expect(row.querySelector(".vst-media-pick-select")).not.toBeNull();
        expect(row.classList.contains("auto-file-box")).toBe(true);
        expect(row.classList.contains("auto-input-flex-wide")).toBe(false);
        expect(
            row.querySelector(".auto-file-input-label > .auto-input-name")
                ?.textContent,
        ).toBe("Audio Upload");
        expect(
            row.querySelector(
                ".auto-file-input-label > .vst-media-pick-actions",
            ),
        ).not.toBeNull();
        expect(
            row.querySelector(".auto-file-label .auto-file-input-filename"),
        ).not.toBeNull();
        const input = row.querySelector<HTMLInputElement>('input[type="file"]');
        expect(input?.accept).toBe("audio/*");
        expect(input?.classList.contains("auto-file")).toBe(true);
    });

    it("falls back to plain upload when inputBrowserHelper is absent", () => {
        savedBrowser = glob.inputBrowserHelper;
        glob.inputBrowserHelper = undefined;
        const row = buildMediaPickRow(
            "Image Upload",
            "image/*",
            ["image"],
            null,
            () => {},
            () => {},
        );
        expect(row.querySelector(".vst-media-pick-select")).toBeNull();
        expect(
            row.querySelector<HTMLInputElement>('input[type="file"]'),
        ).not.toBeNull();
    });

    it("accepts a dropped file as the row's declared media kind", async () => {
        let finishPick: (picked: [string, string]) => void = () => {};
        const picked = new Promise<[string, string]>((resolve) => {
            finishPick = resolve;
        });
        const onFile = jest.fn((data: string, fileName: string) =>
            finishPick([data, fileName]),
        );
        const row = buildMediaPickRow(
            "Image Upload",
            "image/*",
            ["image"],
            null,
            onFile,
            () => {},
        );
        const outerDrop = jest.fn();
        const shell = document.createElement("div");
        shell.addEventListener("drop", outerDrop);
        shell.appendChild(row);
        const dragover = new Event("dragover", {
            bubbles: true,
            cancelable: true,
        });
        const dragoverTransfer = {
            files: [],
            types: ["Files"],
            dropEffect: "none",
        };
        Object.defineProperty(dragover, "dataTransfer", {
            value: dragoverTransfer,
        });
        row.dispatchEvent(dragover);

        expect(dragover.defaultPrevented).toBe(true);
        expect(dragoverTransfer.dropEffect).toBe("copy");

        const event = new Event("drop", {
            bubbles: true,
            cancelable: true,
        });
        Object.defineProperty(event, "dataTransfer", {
            value: {
                files: [
                    new File(["not inspected"], "still.mp4", {
                        type: "video/mp4",
                    }),
                ],
                dropEffect: "none",
            },
        });

        row.dispatchEvent(event);
        const result = await Promise.race([
            picked,
            new Promise<null>((resolve) =>
                setTimeout(() => resolve(null), 100),
            ),
        ]);

        expect(event.defaultPrevented).toBe(true);
        expect(outerDrop).not.toHaveBeenCalled();
        expect(result?.[0]).toMatch(/^vst-browser-media:/);
        expect(resolveBrowserMedia(result?.[0] ?? "")).toMatch(
            /^data:video\/mp4;base64,/,
        );
        expect(result?.[1]).toBe("still.mp4");
        expect(onFile).toHaveBeenCalledTimes(1);
    });

    it("keeps an input-browser server path out of the browser media store", () => {
        const onFile = jest.fn();
        const toDataUrl = jest.fn(async () => "data:image/png;base64,WRONG");
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            toDataUrl,
        });
        const row = buildMediaPickRow(
            "Image Upload",
            "image/*",
            ["image"],
            null,
            onFile,
            () => {},
        );
        const input = row.querySelector<HTMLInputElement>('input[type="file"]');
        if (!input) {
            throw new Error("media input missing");
        }
        input.dataset.filedata = "inputs/references/subject.png";
        input.dataset.filename = "subject.png";

        input.dispatchEvent(new Event("change"));

        expect(onFile).toHaveBeenCalledWith(
            "inputs/references/subject.png",
            "subject.png",
        );
        expect(toDataUrl).not.toHaveBeenCalled();
    });
});

describe("native accordion groups", () => {
    beforeEach(() => {
        localStorage.clear();
        resetRememberedAccordionSections();
    });

    it("expands and remembers sections independently of their enable switch", () => {
        const changed = jest.fn();
        const build = (checked: boolean) => {
            const input = document.createElement("input");
            input.value = "Saved value";
            return buildAccordionSection({
                key: "independent-expansion",
                label: "Optional feature",
                content: input,
                toggle: { checked, title: "Enable feature", onChange: changed },
            });
        };
        for (const checked of [false, true, false]) {
            const built = build(checked);
            const header = built.section.querySelector<HTMLElement>(
                ".input-group-header",
            );
            if (!header) throw new Error("Section header missing");
            header.click();
            expect(header.getAttribute("aria-expanded")).toBe("true");
            expect(built.content.hidden).toBe(false);
            expect(
                built.section.querySelector<HTMLInputElement>(
                    ".group-toggler-switch input",
                )?.checked,
            ).toBe(checked);
            const field =
                built.content.querySelector<HTMLInputElement>("input");
            if (!field) throw new Error("Section field missing");
            expect(field.value).toBe("Saved value");
            expect(field.matches(":disabled")).toBe(!checked);
            const rebuilt = build(!checked);
            expect(rebuilt.content.hidden).toBe(false);
            setFieldDisabled(rebuilt.section, true, "Unavailable");
            const rebuiltHeader = rebuilt.section.querySelector<HTMLElement>(
                ".input-group-header",
            );
            if (!rebuiltHeader) throw new Error("Rebuilt header missing");
            rebuiltHeader.dispatchEvent(
                new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
            );
            expect(rebuilt.content.hidden).toBe(true);
            rebuiltHeader.dispatchEvent(
                new KeyboardEvent("keydown", { key: " ", bubbles: true }),
            );
            expect(rebuilt.content.hidden).toBe(false);
            expect(
                rebuilt.content.querySelector("input")?.matches(":disabled"),
            ).toBe(true);
            rebuiltHeader.click();
        }
    });

    it("marks every accordion with an enable switch for shared placement", () => {
        const built = buildAccordionSection({
            key: "switch-placement",
            label: "Nested feature",
            content: document.createDocumentFragment(),
            toggle: {
                checked: true,
                title: "Toggle nested feature",
                onChange: () => {},
            },
        });

        expect(built.section.classList).toContain(
            "vst-detail-section-has-toggle",
        );
        expect(built.heading.nextElementSibling?.classList).toContain(
            "toggle-switch",
        );
    });
});
