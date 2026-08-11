import { afterEach, beforeEach, describe, expect, it } from "@jest/globals";
import { stubRect } from "./__test_helpers__/dom";
import {
    buildCheckbox,
    buildField,
    buildMediaPickRow,
    buildRepeatingEditor,
    buildStaticSection,
    type RepeatingGroupItem,
    resetRememberedAccordionSections,
} from "./detailWidgets";
import { setTimelineAuthoringSetting } from "./timelineAuthoringSettings";

type MutableGlobal = typeof globalThis & {
    doPopover?: (id: string, e?: Event) => void;
    inputBrowserHelper?: unknown;
};

const glob = globalThis as MutableGlobal;

describe("buildField help popovers", () => {
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
        // The label name stays clean so label-based lookups keep working.
        expect(field.querySelector(".auto-input-name")?.textContent).toBe(
            "Control",
        );
        // Bold field name + the help text (text only, no markup injection).
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
        // Checkbox fields use the same label geometry as every other field.
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
    afterEach(() => {
        // Restore whatever the jest setup seeded (site.js creates a real one).
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
            row.querySelector(".auto-file-label .auto-file-input-filename"),
        ).not.toBeNull();
        // The browser upload path is always present too.
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
});

describe("native detail groups", () => {
    beforeEach(() => {
        localStorage.clear();
        resetRememberedAccordionSections();
    });

    it("builds an always-open input group without accordion behavior", () => {
        const content = document.createElement("div");
        content.textContent = "Permanent fields";
        const built = buildStaticSection({
            key: "static-test",
            label: "Clip",
            content,
            headerAction: {
                label: "⏭︎",
                title: "Skip clip",
                onClick: () => {},
            },
        });
        expect(built.section.classList.contains("input-group")).toBe(true);
        expect(built.section.classList.contains("input-group-open")).toBe(true);
        expect(built.section.dataset.vstStaticKey).toBe("static-test");
        expect(
            built.section.querySelector(".input-group-shrinkable"),
        ).toBeNull();
        expect(
            built.section
                .querySelector(":scope > .input-group-header")
                ?.classList.contains("input-group-noshrink"),
        ).toBe(true);
        expect(built.section.querySelector(".auto-symbol")).toBeNull();
        expect(built.content.hidden).toBe(false);
        expect(
            built.section.querySelector(".vst-detail-repeating-group-action"),
        ).not.toBeNull();
    });

    it("opens the newly appended child through the shared repeater pattern", () => {
        const host = document.createElement("div");
        const items = ["LoRA 0"];
        const render = (): void => {
            host.replaceChildren(
                buildRepeatingEditor({
                    key: "shared-add-test",
                    label: "LoRAs",
                    open: true,
                    defaultActiveIndex: 0,
                    items: items.map((label) => {
                        const editor = document.createElement("div");
                        editor.textContent = `${label} editor`;
                        return { label, editor };
                    }),
                    add: {
                        title: "Add LoRA",
                        className: "test-add",
                        onClick: () => {
                            items.push(`LoRA ${items.length}`);
                            render();
                        },
                    },
                    remove: {
                        title: "Delete LoRA",
                        className: "test-remove",
                    },
                }).section,
            );
        };
        render();
        host.querySelector<HTMLButtonElement>(".test-add")?.click();
        const children = host.querySelectorAll<HTMLElement>(
            ".vst-detail-repeating-group",
        );
        expect(children).toHaveLength(2);
        expect(children[0].classList.contains("input-group-closed")).toBe(true);
        expect(children[1].classList.contains("input-group-open")).toBe(true);
        expect(children[1].querySelector(".header-label")?.textContent).toBe(
            "LoRA 1",
        );
        expect(
            children[1].querySelector<HTMLElement>(
                ".vst-detail-repeating-group-content",
            )?.hidden,
        ).toBe(false);
    });

    it("restores sidebar scroll after a repeating-item structural action settles", () => {
        const frames: FrameRequestCallback[] = [];
        const original = window.requestAnimationFrame;
        window.requestAnimationFrame = (callback: FrameRequestCallback) => {
            frames.push(callback);
            return frames.length;
        };
        const body = document.createElement("div");
        body.className = "vst-detail-body";
        const host = document.createElement("div");
        body.appendChild(host);
        document.body.appendChild(body);
        const items = ["LoRA 0"];
        const render = (): void => {
            host.replaceChildren(
                buildRepeatingEditor({
                    key: "scroll-preservation-test",
                    label: "LoRAs",
                    open: true,
                    defaultActiveIndex: 0,
                    items: items.map((label) => ({ label })),
                    add: {
                        title: "Add LoRA",
                        className: "test-add",
                        onClick: () => {
                            items.push(`LoRA ${items.length}`);
                            render();
                        },
                    },
                    remove: {
                        title: "Delete LoRA",
                        className: "test-remove",
                    },
                }).section,
            );
        };

        try {
            render();
            body.scrollTop = 140;
            host.querySelector<HTMLButtonElement>(".test-add")?.click();
            expect(frames).toHaveLength(1);
            body.scrollTop = 0;
            frames[0](0);
            expect(body.scrollTop).toBe(140);
        } finally {
            if (original) {
                window.requestAnimationFrame = original;
            } else {
                Reflect.deleteProperty(window, "requestAnimationFrame");
            }
        }
    });

    it("keeps the Add action anchored when a structural edit grows content above it", () => {
        const frames: FrameRequestCallback[] = [];
        const original = window.requestAnimationFrame;
        window.requestAnimationFrame = (callback: FrameRequestCallback) => {
            frames.push(callback);
            return frames.length;
        };
        const body = document.createElement("div");
        body.className = "vst-detail-body";
        const host = document.createElement("div");
        body.appendChild(host);
        document.body.appendChild(body);
        const items = ["LoRA 0"];
        const render = (): void => {
            host.replaceChildren(
                buildRepeatingEditor({
                    key: "add-anchor-test",
                    label: "LoRAs",
                    items: items.map((label) => ({ label })),
                    add: {
                        title: "Add LoRA",
                        className: "test-add",
                        onClick: () => {
                            items.push(`LoRA ${items.length}`);
                            render();
                        },
                    },
                    remove: {
                        title: "Delete LoRA",
                        className: "test-remove",
                    },
                }).section,
            );
            const add = host.querySelector<HTMLElement>(".test-add");
            if (!add) {
                throw new Error("Add LoRA missing");
            }
            stubRect(add, 0, 0, items.length === 1 ? 300 : 420);
        };

        try {
            render();
            body.scrollTop = 100;
            host.querySelector<HTMLButtonElement>(".test-add")?.click();
            frames[0](0);
            expect(body.scrollTop).toBe(220);
        } finally {
            if (original) {
                window.requestAnimationFrame = original;
            } else {
                Reflect.deleteProperty(window, "requestAnimationFrame");
            }
        }
    });

    it("keeps an active repeating item minimized through a rebuild", () => {
        const host = document.createElement("div");
        const render = (): void => {
            host.replaceChildren(
                buildRepeatingEditor({
                    key: "minimized-rebuild-test",
                    label: "Stages",
                    open: true,
                    items: [{ label: "Stage 0", active: true }],
                    editorForItem: () => document.createElement("div"),
                    add: {
                        title: "Add stage",
                        className: "test-add",
                        onClick: () => {},
                    },
                    remove: {
                        title: "Delete stage",
                        className: "test-remove",
                    },
                }).section,
            );
        };

        render();
        host.querySelector<HTMLElement>(
            ".vst-detail-repeating-group-header",
        )?.click();
        render();

        expect(
            host
                .querySelector(".vst-detail-repeating-group")
                ?.classList.contains("input-group-closed"),
        ).toBe(true);
    });

    it("opens an empty repeater so its Add action is visible by default", () => {
        const built = buildRepeatingEditor({
            key: "empty-add-test",
            label: "References",
            items: [],
            add: {
                title: "Add reference",
                label: "+ Add Reference Image",
                className: "test-add",
                onClick: () => {},
            },
            remove: {
                title: "Delete reference",
                className: "test-remove",
            },
        });

        expect(built.section.classList.contains("input-group-open")).toBe(true);
        expect(built.content.hidden).toBe(false);
        const add = built.content.querySelector<HTMLButtonElement>(".test-add");
        expect(add).not.toBeNull();
        expect(add?.textContent).toBe("+ Add Reference Image");
    });

    it("opens a repeating parent by default when it has children", () => {
        const built = buildRepeatingEditor({
            key: "populated-parent-test",
            label: "References",
            items: [{ label: "Reference 0" }],
            add: {
                title: "Add reference",
                className: "test-add",
                onClick: () => {},
            },
            remove: {
                title: "Delete reference",
                className: "test-remove",
            },
        });

        expect(built.section.classList.contains("input-group-open")).toBe(true);
        expect(built.content.hidden).toBe(false);
    });

    it("restores expanded children after an in-memory reload", () => {
        const item = {
            label: "Reference 0",
            stateKey: "reference-id-0",
        } as unknown as RepeatingGroupItem;
        const build = (): HTMLElement =>
            buildRepeatingEditor({
                key: "child-reload-test",
                label: "References",
                items: [item],
                add: {
                    title: "Add reference",
                    className: "test-add",
                    onClick: () => {},
                },
                remove: {
                    title: "Delete reference",
                    className: "test-remove",
                },
            }).section;

        const beforeReload = build();
        beforeReload
            .querySelector<HTMLElement>(".vst-detail-repeating-group-header")
            ?.click();
        resetRememberedAccordionSections();
        const afterReload = build();

        expect(
            afterReload
                .querySelector(".vst-detail-repeating-group")
                ?.classList.contains("input-group-open"),
        ).toBe(true);

        const activeItem = {
            label: "Reference 1",
            stateKey: "reference-id-1",
            active: true,
        } as unknown as RepeatingGroupItem;
        const buildActive = (): HTMLElement =>
            buildRepeatingEditor({
                key: "child-closed-reload-test",
                label: "References",
                items: [activeItem],
                add: {
                    title: "Add reference",
                    className: "test-add",
                    onClick: () => {},
                },
                remove: {
                    title: "Delete reference",
                    className: "test-remove",
                },
            }).section;
        buildActive()
            .querySelector<HTMLElement>(".vst-detail-repeating-group-header")
            ?.click();
        resetRememberedAccordionSections();

        expect(
            buildActive()
                .querySelector(".vst-detail-repeating-group")
                ?.classList.contains("input-group-closed"),
        ).toBe(true);
    });

    it("builds a closed child's editor when the child is expanded", () => {
        const editor = document.createElement("div");
        editor.className = "test-editor";
        const built = buildRepeatingEditor({
            key: "closed-editor-test",
            label: "Keyframes",
            items: [{ label: "Keyframe 0" }],
            editorForItem: () => editor,
            add: {
                title: "Add keyframe",
                className: "test-add",
                onClick: () => {},
            },
            remove: {
                title: "Delete keyframe",
                className: "test-remove",
            },
        });

        expect(built.section.querySelector(".test-editor")).toBeNull();
        built.section
            .querySelector<HTMLElement>(".vst-detail-repeating-group-header")
            ?.click();
        expect(built.section.querySelector(".test-editor")).not.toBeNull();
    });

    it("puts each item's title on its header", () => {
        const section = buildRepeatingEditor({
            key: "item-title-test",
            label: "References",
            items: [
                { label: "R0", title: "Edit reference image 0" },
                { label: "R1" },
            ],
            add: {
                title: "Add reference",
                className: "test-add",
                onClick: () => {},
            },
            remove: { title: "Delete reference", className: "test-remove" },
        }).section;

        const headers = section.querySelectorAll<HTMLElement>(
            ".vst-detail-repeating-group-header",
        );
        expect(headers[0].title).toBe("Edit reference image 0");
        expect(headers[1].title).toBe("");
    });

    it("keeps selected repeater editors open when Auto-collapse is disabled", () => {
        setTimelineAuthoringSetting("autoCollapse", false);
        const host = document.createElement("div");
        let selectedIndex = 0;
        const render = (): void => {
            host.replaceChildren(
                buildRepeatingEditor({
                    key: "multi-open-selection-test",
                    label: "Stages",
                    open: true,
                    items: ["Stage 0", "Stage 1"].map((label, index) => ({
                        label,
                        active: index === selectedIndex,
                        onSelect: () => {
                            selectedIndex = index;
                            render();
                        },
                    })),
                    editorForItem: (index) => {
                        const editor = document.createElement("div");
                        editor.className = "test-editor";
                        editor.textContent = `Editor ${index}`;
                        return editor;
                    },
                    add: {
                        title: "Add stage",
                        className: "test-add",
                        onClick: () => {},
                    },
                    remove: {
                        title: "Delete stage",
                        className: "test-remove",
                    },
                }).section,
            );
        };

        render();
        host.querySelectorAll<HTMLElement>(
            ".vst-detail-repeating-group .input-group-header",
        )[1]?.click();

        const children = host.querySelectorAll<HTMLElement>(
            ".vst-detail-repeating-group",
        );
        expect(children).toHaveLength(2);
        expect(
            Array.from(children).every((child) =>
                child.classList.contains("input-group-open"),
            ),
        ).toBe(true);
        expect(host.querySelectorAll(".test-editor")).toHaveLength(2);
        expect(children[0].textContent).toContain("Editor 0");
        expect(children[1].textContent).toContain("Editor 1");
    });
});
