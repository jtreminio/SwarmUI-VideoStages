import { afterEach, expect, it, jest } from "@jest/globals";
import { buildMultiSelectPicker } from "./multiSelectPicker";

afterEach(() => {
    document.body.replaceChildren();
    jest.restoreAllMocks();
});

it.each([
    [100, 134],
    [700, 396],
])("positions a picker at %ipx within the viewport", (top, expectedTop) => {
    const picker = buildMultiSelectPicker({
        label: "Folders",
        placeholder: "Choose folders",
        filterPlaceholder: "Filter folders",
        options: [{ value: "motion", label: "Motion" }],
        selected: [],
        onChange: jest.fn(),
    });
    document.body.append(picker);
    const trigger = picker.querySelector<HTMLElement>("[role=combobox]");
    const panel = picker.querySelector<HTMLElement>(
        "[data-vst-multi-select-panel]",
    );
    if (!trigger || !panel) throw new Error("Missing picker controls");
    jest.spyOn(trigger, "getBoundingClientRect").mockReturnValue({
        top,
        bottom: top + 30,
        left: 100,
        right: 400,
        width: 300,
        height: 30,
    } as DOMRect);
    jest.spyOn(panel, "offsetHeight", "get").mockReturnValue(300);
    trigger.click();
    expect(panel.style.top).toBe(`${expectedTop}px`);
    expect(panel.style.left).toBe("100px");
    expect(panel.hidden).toBe(false);
    picker.dispatchEvent(
        new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
    );
    expect(panel.hidden).toBe(true);
});
