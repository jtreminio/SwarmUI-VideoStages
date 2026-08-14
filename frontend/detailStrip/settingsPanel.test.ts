import { describe, expect, it, jest } from "@jest/globals";
import {
    detail,
    detailStripHarness,
    fieldByLabel,
    sliderNumberByLabel,
} from "../__test_helpers__/detailStrip";
import { dimensionsFor } from "../dimensionPresets";
import { snapDimensions } from "../dimensionSnap";
import { setSelection } from "../selection";

describe("detail strip timeline settings panel", () => {
    const h = detailStripHarness();

    it("uses SwarmUI POT stops and updates side-length dimensions live", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        const ratio =
            fieldByLabel("Aspect Ratio").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!ratio) {
            throw new Error("aspect-ratio select missing");
        }
        ratio.value = "16:9";
        ratio.dispatchEvent(new Event("change", { bubbles: true }));

        const number = sliderNumberByLabel("Side Length");
        const range = number
            .closest(".auto-slider-box")
            ?.querySelector<HTMLInputElement>("input.auto-slider-range");
        const calculated = detail()?.querySelector<HTMLElement>(
            ".vst-settings-calculated-dims",
        );
        if (!range || !calculated) {
            throw new Error("side-length POT slider missing");
        }
        expect(range.dataset.ispot).toBe("true");
        expect(range.step).toBe("1");
        expect(calculated.textContent).toBe("1344 × 768");
        expect(calculated.textContent).not.toContain("multiples");

        const potPosition = (value: number): string =>
            `${Math.round(((Math.log2(value) - 8) / 4) * 4096)}`;
        const stops = [768, 896, 1024, 1280];
        for (const stop of stops) {
            range.value = potPosition(stop);
            range.dispatchEvent(new Event("input", { bubbles: true }));
            expect(number.value).toBe(`${stop}`);
        }
        expect(calculated.textContent).toBe("1696 × 960");
    });

    it("applies aspect ratio plus side length and reveals dimensions only for Custom", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        const select =
            fieldByLabel("Aspect Ratio").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!select) {
            throw new Error("aspect-ratio select missing");
        }
        select.value = "2:3";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        let parsed = JSON.parse(
            document.querySelector<HTMLTextAreaElement>("#input_videostages")
                ?.value ?? "{}",
        );
        expect(parsed.width).toBe(832);
        expect(parsed.height).toBe(1216);
        expect(sliderNumberByLabel("Side Length").disabled).toBe(false);
        expect(sliderNumberByLabel("Side Length").step).toBe("32");
        expect(
            Array.from(
                document.querySelectorAll<HTMLElement>(
                    ".vst-detail .vst-detail-field-label",
                ),
            ).map((element) => element.textContent),
        ).not.toEqual(expect.arrayContaining(["Width", "Height"]));

        const ratio =
            fieldByLabel("Aspect Ratio").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!ratio) {
            throw new Error("aspect-ratio select missing after render");
        }
        ratio.value = "custom";
        ratio.dispatchEvent(new Event("change", { bubbles: true }));
        parsed = JSON.parse(
            document.querySelector<HTMLTextAreaElement>("#input_videostages")
                ?.value ?? "{}",
        );
        expect(parsed.width).toBe(832);
        expect(parsed.height).toBe(1216);
        expect(sliderNumberByLabel("Width").step).toBe("32");
        expect(sliderNumberByLabel("Height").step).toBe("32");
        expect(
            detail()?.querySelectorAll(
                ".vst-stage-slider input.auto-slider-range",
            ),
        ).toHaveLength(2);
        expect(
            document.querySelector(
                'input[data-vst-focus-key="settings-side-length"]',
            ),
        ).toBeNull();
        expect(
            detail()?.querySelector(".vst-settings-calculated-dims"),
        ).toBeNull();
        expect(h.refreshSpy).toHaveBeenCalled();
    });

    it("keeps Custom selected after editing a clip and reopening settings", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        const ratio =
            fieldByLabel("Aspect Ratio").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!ratio) {
            throw new Error("aspect-ratio select missing");
        }
        ratio.value = "custom";
        ratio.dispatchEvent(new Event("change", { bubbles: true }));

        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        setSelection({ kind: "none" });

        expect(
            fieldByLabel("Aspect Ratio").querySelector<HTMLSelectElement>(
                "select",
            )?.value,
        ).toBe("custom");
    });

    it("snaps aspect-ratio and side-length changes to the selected grid", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        const snap =
            fieldByLabel("Dimension Snap").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!snap) {
            throw new Error("dimension-snap select missing");
        }
        expect(snap.value).toBe("disabled");
        snap.value = "64";
        snap.dispatchEvent(new Event("change", { bubbles: true }));

        const ratio =
            fieldByLabel("Aspect Ratio").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!ratio) {
            throw new Error("aspect-ratio select missing");
        }
        ratio.value = "16:9";
        ratio.dispatchEvent(new Event("change", { bubbles: true }));

        jest.useFakeTimers();
        const sideLength = sliderNumberByLabel("Side Length");
        sideLength.value = "1050";
        sideLength.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);

        const raw = dimensionsFor("16:9", 1056);
        if (!raw) {
            throw new Error("16:9 dimensions missing");
        }
        const expected = snapDimensions(raw.width, raw.height, 64);
        const stored = JSON.parse(
            document.querySelector<HTMLTextAreaElement>("#input_videostages")
                ?.value ?? "{}",
        ) as Record<string, unknown>;
        expect({ width: stored.width, height: stored.height }).toEqual(
            expected,
        );
    });

    it("waits for a custom dimension field to lose focus before snapping", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        const ratio =
            fieldByLabel("Aspect Ratio").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!ratio) {
            throw new Error("aspect-ratio select missing");
        }
        ratio.value = "custom";
        ratio.dispatchEvent(new Event("change", { bubbles: true }));

        const snap =
            fieldByLabel("Dimension Snap").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!snap) {
            throw new Error("dimension-snap select missing");
        }
        snap.value = "64";
        snap.dispatchEvent(new Event("change", { bubbles: true }));

        jest.useFakeTimers();
        const width = sliderNumberByLabel("Width");
        width.focus();
        width.value = "1232";
        width.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        let stored = JSON.parse(
            document.querySelector<HTMLTextAreaElement>("#input_videostages")
                ?.value ?? "{}",
        ) as Record<string, unknown>;
        expect(stored.width).toBe(1024);
        expect(stored.height).toBe(1024);

        width.blur();
        jest.advanceTimersByTime(200);
        stored = JSON.parse(
            document.querySelector<HTMLTextAreaElement>("#input_videostages")
                ?.value ?? "{}",
        ) as Record<string, unknown>;
        expect({ width: stored.width, height: stored.height }).toEqual(
            snapDimensions(1232, 1024, 64),
        );
    });
});
