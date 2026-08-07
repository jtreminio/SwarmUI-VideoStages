import { afterEach, beforeEach, describe, expect, it } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCatalog,
    testArchitectureCatalogDto,
    testAuthoringTransactionSnapshot,
} from "../__test_helpers__/architectureFixtures";
import {
    mountPromptBox,
    mountSelect,
    mountVideoFps,
    mountVideoStagesData,
} from "../__test_helpers__/dom";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import {
    __resetPersistenceForTests,
    getClips,
    getState,
} from "../persistence/repository";
import type { DetailStripContext } from "./context";
import { buildRefSection } from "./refPanel";

describe("buildRefSection", () => {
    beforeEach(() => {
        __resetPersistenceForTests();
        document.body.innerHTML = "";
        mountPromptBox("");
    });

    afterEach(() => {
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests(null);
        document.body.innerHTML = "";
    });

    it("uses the core Video FPS for the reference frame limit", () => {
        mountVideoFps(16);
        mountVideoStagesData({
            clips: [
                {
                    duration: 5,
                    stages: [{ model: "ltx-2.3.safetensors" }],
                    frameRefs: [{ source: "Base", frame: 1 }],
                },
            ],
        });

        const catalog = testArchitectureCatalog();
        catalog.entries[0].enhancements = {
            referencePositions: ["any"],
        };
        const body = document.createElement("div");
        body.className = "vst-detail-body";
        const state = { ...getState(), clips: getClips() };
        body.appendChild(
            buildRefSection(
                {
                    authoring: () => testAuthoringTransactionSnapshot(catalog),
                    buildClampedNumber: () => document.createElement("input"),
                } as unknown as DetailStripContext,
                0,
                0,
                state.clips,
                state.fps,
            ),
        );
        const field = Array.from(
            body.querySelectorAll<HTMLElement>(".vst-detail-field"),
        ).find((el) =>
            el
                .querySelector(".vst-detail-field-label")
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

    it("locks bounded references to frame one while allowing first/last editing", async () => {
        const catalog = testArchitectureCatalog();
        const model = catalog.entries[0];
        model.enhancements = {
            referencePositions: ["first", "last"],
        };
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(catalog),
        });
        await loadAuthoritativeArchitectureCatalog();
        mountSelect("input_videomodel", {
            options: [model.value],
            value: model.value,
        });
        mountVideoStagesData({
            clips: [
                {
                    duration: 5,
                    stages: [{ model: model.value }],
                    frameRefs: [{ source: "Upload", frame: 1, fromEnd: false }],
                },
            ],
        });

        const body = document.createElement("div");
        body.className = "vst-detail-body";
        const state = { ...getState(), clips: getClips() };
        body.appendChild(
            buildRefSection(
                {
                    authoring: () => testAuthoringTransactionSnapshot(catalog),
                    buildClampedNumber: () => document.createElement("input"),
                } as unknown as DetailStripContext,
                0,
                0,
                state.clips,
                state.fps,
            ),
        );
        const frame = body.querySelector<HTMLInputElement>(
            '[data-vst-focus-key="ref-0-frame"]',
        );
        const fromEnd = Array.from(
            body.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'),
        ).find((input) =>
            input
                .closest(".auto-input")
                ?.textContent?.includes("Count from clip end"),
        );

        expect(frame?.min).toBe("1");
        expect(frame?.max).toBe("1");
        expect(fromEnd?.disabled).toBe(false);
        expect(body.textContent).toContain(
            "This clip accepts an image only at the first or final frame.",
        );
    });

    it("lets a persisted unsupported endpoint be repaired", async () => {
        const catalog = testArchitectureCatalog();
        const model = catalog.entries[0];
        model.enhancements = {
            referencePositions: ["first"],
        };
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(catalog),
        });
        await loadAuthoritativeArchitectureCatalog();
        mountSelect("input_videomodel", {
            options: [model.value],
            value: model.value,
        });
        mountVideoStagesData({
            clips: [
                {
                    duration: 5,
                    stages: [{ model: model.value }],
                    frameRefs: [{ source: "Upload", frame: 1, fromEnd: true }],
                },
            ],
        });

        const body = document.createElement("div");
        body.className = "vst-detail-body";
        const state = { ...getState(), clips: getClips() };
        body.appendChild(
            buildRefSection(
                {
                    authoring: () => testAuthoringTransactionSnapshot(catalog),
                    buildClampedNumber: () => document.createElement("input"),
                } as unknown as DetailStripContext,
                0,
                0,
                state.clips,
                state.fps,
            ),
        );
        const fromEnd = Array.from(
            body.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'),
        ).find((input) =>
            input
                .closest(".auto-input")
                ?.textContent?.includes("Count from clip end"),
        );

        expect(fromEnd?.disabled).toBe(false);
        expect(body.textContent).toContain(
            "This clip accepts an image only at the first frame.",
        );
        expect(body.textContent).toContain(
            "This clip supports only the first frame.",
        );
        expect(body.textContent).not.toContain("or final frame");
    });
});
