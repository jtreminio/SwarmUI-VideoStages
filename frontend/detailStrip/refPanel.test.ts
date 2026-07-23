import { afterEach, beforeEach, describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "../__test_helpers__/architectureFixtures";
import { mountPromptBox, mountVideoStagesData } from "../__test_helpers__/dom";
import { createCapabilityViewResolver } from "../architectures/policy";
import { __resetPersistenceForTests, getClips } from "../persistence";
import type { DetailStripContext } from "./context";
import { buildRefBody } from "./refPanel";

describe("buildRefBody", () => {
    beforeEach(() => {
        __resetPersistenceForTests();
        document.body.innerHTML = "";
        mountPromptBox("");
    });

    afterEach(() => {
        document.body.innerHTML = "";
    });

    it("uses the stored custom FPS for the reference frame limit", () => {
        mountVideoStagesData({
            fps: 16,
            clips: [
                {
                    duration: 5,
                    stages: [{}],
                    refs: [{ source: "Base", frame: 1 }],
                },
            ],
        });

        const body = buildRefBody(
            {
                capabilities: () =>
                    createCapabilityViewResolver(testArchitectureCatalog()),
                buildClampedNumber: () => document.createElement("input"),
            } as unknown as DetailStripContext,
            { kind: "ref", clipIdx: 0, refIdx: 0 },
            getClips(),
        );
        const field = Array.from(
            body.querySelectorAll<HTMLElement>(".vst-audio-field"),
        ).find((el) =>
            el
                .querySelector(".vst-audio-field-label")
                ?.textContent?.startsWith("Attach at Frame"),
        );

        expect(field?.querySelector<HTMLInputElement>("input")?.max).toBe("81");
        expect(
            field?.querySelector(".sui-info-popover")?.textContent,
        ).toContain("Frame 1 is the first frame");
        expect(
            field?.querySelector(".sui-info-popover")?.textContent,
        ).not.toContain("Frame 0 is the first frame");
    });
});
