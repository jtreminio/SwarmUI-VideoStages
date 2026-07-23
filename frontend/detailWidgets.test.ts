import { afterEach, describe, expect, it } from "@jest/globals";
import { buildCheckbox, buildField, buildMediaPickRow } from "./detailWidgets";

type MutableGlobal = typeof globalThis & {
    doPopover?: (id: string, e?: Event) => void;
    inputBrowserHelper?: unknown;
};

const glob = globalThis as MutableGlobal;

describe("buildField help popovers", () => {
    afterEach(() => {
        delete glob.doPopover;
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
        expect(check.querySelector(".info-popover-button")).not.toBeNull();
        expect(check.querySelector(".sui-popover")?.id).toMatch(
            /^popover_vst_reuse-audio_\d+$/,
        );
        // Checkbox label text stays clean.
        expect(check.querySelector(".auto-input-name")?.textContent).toBe(
            "Reuse Audio",
        );
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
        // The browser upload path is always present too.
        expect(
            row.querySelector<HTMLInputElement>('input[type="file"]')?.accept,
        ).toBe("audio/*");
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
