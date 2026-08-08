import { describe, expect, it } from "@jest/globals";
import {
    detail,
    detailStripHarness,
    fieldByLabel,
    sliderNumberByLabel,
} from "../__test_helpers__/detailStrip";

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
});
