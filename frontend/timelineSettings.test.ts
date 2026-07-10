import { beforeEach, describe, expect, it } from "@jest/globals";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
import { __resetPersistenceForTests, getState } from "./persistence";
import { readDataParam } from "./swarmInputs";
import {
    createTimelineSettings,
    type TimelineSettings,
} from "./timelineSettings";
import { renderTimeline } from "./timelineView";

const addPromptInput = (json = '{"clips":[]}'): HTMLTextAreaElement => {
    mountPromptBox("");
    return mountVideoStagesData(json);
};

const mountInput = (id: string, value: string): void => {
    const input = document.createElement("input");
    input.id = id;
    input.value = value;
    document.body.appendChild(input);
};

const parseSection = (): Record<string, unknown> =>
    JSON.parse(readDataParam()) as Record<string, unknown>;

describe("timelineSettings", () => {
    let body: HTMLElement;
    let settings: TimelineSettings;

    const render = (): void => {
        const state = getState();
        renderTimeline(body, state.clips, {
            width: state.width,
            height: state.height,
            fps: state.fps,
            dimsExplicit: state.dimsExplicit,
            fpsExplicit: state.fpsExplicit,
            onOpenSettings: (anchor) => settings.open(anchor),
        });
    };

    const chip = (): HTMLElement =>
        body.querySelector<HTMLElement>("[data-vst-settings]") as HTMLElement;
    const inspector = (): HTMLElement | null =>
        document.querySelector<HTMLElement>(".vst-settings-inspector");
    const resSelect = (): HTMLSelectElement =>
        inspector()?.querySelector("select") as HTMLSelectElement;
    const numInputs = (): HTMLInputElement[] =>
        Array.from(
            inspector()?.querySelectorAll<HTMLInputElement>(
                "input.vst-settings-num",
            ) ?? [],
        );
    const fpsCheck = (): HTMLInputElement =>
        inspector()?.querySelector(
            'input[type="checkbox"]',
        ) as HTMLInputElement;

    const openPopover = (): void => {
        chip().click();
    };

    beforeEach(() => {
        __resetPersistenceForTests();
        document.body.innerHTML = "";
        addPromptInput();
        body = document.createElement("div");
        body.id = "videostages-timeline-body";
        document.body.appendChild(body);
        settings = createTimelineSettings(render);
        render();
    });

    it("opens a popover from the chip", () => {
        expect(inspector()).toBeNull();
        openPopover();
        expect(inspector()).not.toBeNull();
    });

    it("writes explicit width/height when a preset is selected", () => {
        openPopover();
        const select = resSelect();
        select.value = "512x768";
        select.dispatchEvent(new Event("change"));

        const parsed = parseSection();
        expect(parsed.width).toBe(512);
        expect(parsed.height).toBe(768);
        expect(getState().dimsExplicit).toBe(true);
    });

    it("persists custom dimensions and keeps them editable", () => {
        openPopover();
        const select = resSelect();
        select.value = "custom";
        select.dispatchEvent(new Event("change"));

        const [widthInput, heightInput] = numInputs();
        expect(widthInput.disabled).toBe(false);
        widthInput.value = "640";
        widthInput.dispatchEvent(new Event("input"));
        heightInput.value = "384";
        heightInput.dispatchEvent(new Event("input"));

        const parsed = parseSection();
        expect(parsed.width).toBe(640);
        expect(parsed.height).toBe(384);
    });

    it("removes width/height from the JSON when switching to inherit", () => {
        // Seed explicit dims first.
        openPopover();
        let select = resSelect();
        select.value = "512x768";
        select.dispatchEvent(new Event("change"));
        expect("width" in parseSection()).toBe(true);

        // Popover persists across re-render; switch to inherit.
        select = resSelect();
        select.value = "inherit";
        select.dispatchEvent(new Event("change"));

        const parsed = parseSection();
        expect("width" in parsed).toBe(false);
        expect("height" in parsed).toBe(false);
        expect(getState().dimsExplicit).toBe(false);
    });

    it("round-trips the custom FPS checkbox and value", () => {
        openPopover();
        const check = fpsCheck();
        expect(check.checked).toBe(false);
        check.checked = true;
        check.dispatchEvent(new Event("change"));

        const fpsInput = numInputs()[2];
        expect(fpsInput.disabled).toBe(false);
        fpsInput.value = "30";
        fpsInput.dispatchEvent(new Event("input"));

        expect(parseSection().fps).toBe(30);
        expect(getState().fpsExplicit).toBe(true);

        // Unchecking removes it again.
        check.checked = false;
        check.dispatchEvent(new Event("change"));
        expect("fps" in parseSection()).toBe(false);
    });

    it("updates the chip readout after a change", () => {
        openPopover();
        const select = resSelect();
        select.value = "512x768";
        select.dispatchEvent(new Event("change"));

        expect(chip().textContent).toContain("512×768");
    });

    it("flips above the anchor when it would overflow the viewport bottom", () => {
        const anchorEl = chip();
        anchorEl.getBoundingClientRect = () =>
            ({ top: 700, bottom: 720, left: 10, right: 90 }) as DOMRect;
        const original = Object.getOwnPropertyDescriptor(
            HTMLElement.prototype,
            "offsetHeight",
        ) as PropertyDescriptor;
        Object.defineProperty(HTMLElement.prototype, "offsetHeight", {
            configurable: true,
            get: () => 200,
        });
        try {
            openPopover();
            expect(inspector()?.style.top).toBe("494px");
        } finally {
            Object.defineProperty(
                HTMLElement.prototype,
                "offsetHeight",
                original,
            );
        }
    });

    it("shows inherited values that follow the core inputs", () => {
        document.body.innerHTML = "";
        addPromptInput();
        mountInput("input_width", "800");
        mountInput("input_height", "600");
        mountInput("input_videofps", "12");
        body = document.createElement("div");
        body.id = "videostages-timeline-body";
        document.body.appendChild(body);
        settings = createTimelineSettings(render);
        render();

        openPopover();
        const select = resSelect();
        expect(
            Array.from(select.options).find((o) => o.value === "inherit")
                ?.textContent,
        ).toContain("800×600");
        const [widthInput, heightInput, fpsInput] = numInputs();
        expect(widthInput.value).toBe("800");
        expect(widthInput.disabled).toBe(true);
        expect(heightInput.value).toBe("600");
        expect(fpsInput.value).toBe("12");
        expect(fpsInput.disabled).toBe(true);
    });
});
