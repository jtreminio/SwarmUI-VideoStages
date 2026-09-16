import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCatalog,
    testArchitectureCatalogDto,
} from "../__test_helpers__/architectureFixtures";
import { committedClips, editorHarness } from "../__test_helpers__/editor";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import type { ArchitectureModelCatalog } from "../architectures/types";
import { H3_TEXT_ENCODER_FEATURE } from "../generatedMiniMaxTextEncoder";
import { H3_ATTENTION_WINDOW_FEATURE } from "../h3AttentionWindow";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";

const minimaxCatalog = (): ArchitectureModelCatalog => {
    const catalog = testArchitectureCatalog();
    catalog.architectures[0].id = "minimax";
    catalog.architectures[0].label = "MiniMax H3";
    catalog.architectures[0].capabilities.features.push("stageAttention");
    for (const entry of catalog.entries) {
        entry.architectureId = "minimax";
        entry.modelProfileId = "minimax-h3";
        entry.modelClassId = "minimax-h3";
    }
    return catalog;
};

const backend = globalThis as typeof globalThis & {
    currentBackendFeatureSet?: string[];
};

const loadCatalog = async (
    catalog: ArchitectureModelCatalog,
): Promise<void> => {
    resetArchitectureCatalogForTests();
    setVideoStagesHostBridgeForTests({
        ...createDefaultVideoStagesHostBridge(),
        requestJson: async () => testArchitectureCatalogDto(catalog),
    });
    await loadAuthoritativeArchitectureCatalog();
};

describe("MiniMax H3 controls", () => {
    const h = editorHarness();

    afterEach(() => {
        delete backend.currentBackendFeatureSet;
        Reflect.deleteProperty(globalThis, "installFeatureById");
    });

    it("renders stage attention and commits seconds independently when JuanAttn is installed", async () => {
        backend.currentBackendFeatureSet = [H3_ATTENTION_WINDOW_FEATURE];
        await loadCatalog(minimaxCatalog());
        h.setup([
            { duration: 4, stages: [{}, { h3AttentionWindowSeconds: 4 }] },
        ]);
        const body = h.item("stages", 0);
        const labels = Array.from(
            body.querySelectorAll<HTMLElement>(".auto-input-name"),
        ).map((label) => label.textContent);
        expect(labels.indexOf("Attention window (s)")).toBeGreaterThan(
            labels.indexOf("Steps"),
        );
        const number = h.field<HTMLInputElement>(
            "stages",
            "Attention window (s)",
            "input.auto-slider-number",
        );
        expect(number.closest(".vst-stage-attention")).not.toBeNull();
        expect(number.value).toBe("0");
        number.value = "2.5";
        number.dispatchEvent(new Event("input", { bubbles: true }));
        h.editor.flushPending();
        expect(
            committedClips()[0].stages.map(
                (stage) => stage.h3AttentionWindowSeconds,
            ),
        ).toEqual([2.5, 4]);
        const last = h.field<HTMLInputElement>(
            "stages",
            "Attention window (s)",
            "input.auto-slider-number",
            1,
        );
        last.value = "3.5";
        last.dispatchEvent(new Event("input", { bubbles: true }));
        h.section("stages")
            .querySelector<HTMLButtonElement>("[data-testid=add-stages]")
            ?.click();
        expect(
            committedClips()[0].stages.map(
                (stage) => stage.h3AttentionWindowSeconds,
            ),
        ).toEqual([2.5, 3.5, 3.5]);
    });

    it("gates attention by architecture and enables it after the backend gains JuanAttn", async () => {
        await loadCatalog(minimaxCatalog());
        h.setup([{ duration: 4, stages: [{}] }]);
        expect(
            h.field("prompt", "Duration (s)", "input.auto-slider-number").value,
        ).toBe("4");
        expect(
            h.section("stages").querySelector("[data-vst-h3-attention-window]"),
        ).toBeNull();
        backend.currentBackendFeatureSet = [H3_ATTENTION_WINDOW_FEATURE];
        await loadCatalog(testArchitectureCatalog());
        h.editor.refresh();
        expect(
            h.field<HTMLSelectElement>("prompt", "Reference resize", "select")
                .value,
        ).toBe("crop");
        expect(
            h.section("stages").querySelector("[data-vst-h3-attention-window]"),
        ).toBeNull();
        await loadCatalog(minimaxCatalog());
        h.editor.refresh();
        const number = h.field<HTMLInputElement>(
            "stages",
            "Attention window (s)",
            "input.auto-slider-number",
        );
        number.value = "3";
        number.dispatchEvent(new Event("input", { bubbles: true }));
        h.editor.flushPending();
        expect(committedClips()[0].stages[0].h3AttentionWindowSeconds).toBe(3);
    });

    it("renders the clip text encoder with help and commits the selection", async () => {
        backend.currentBackendFeatureSet = [
            H3_ATTENTION_WINDOW_FEATURE,
            H3_TEXT_ENCODER_FEATURE,
        ];
        await loadCatalog(minimaxCatalog());
        h.setup([{ duration: 4, stages: [{}] }]);
        const body = h.section("prompt");
        const labels = Array.from(
            body.querySelectorAll<HTMLElement>(".auto-input-name"),
        ).map((label) => label.textContent);
        expect(labels.indexOf("Text Encoder")).toBeGreaterThan(
            labels.indexOf("Reference resize"),
        );
        const select = h.field<HTMLSelectElement>(
            "prompt",
            "Text Encoder",
            "select",
        );
        expect(
            Array.from(select.options).map((option) => option.value),
        ).toEqual(["default", "8b", "4b"]);
        const help = select
            .closest(".vst-detail-field")
            ?.querySelector<HTMLElement>(".sui-info-popover");
        expect(help?.textContent).toContain(
            "download the matching projection automatically",
        );
        const repo = help?.querySelector<HTMLAnchorElement>("a");
        expect(repo?.href).toBe(
            "https://github.com/nicolab28/ComfyUI-ClipProj",
        );
        expect(repo?.target).toBe("_blank");
        select.value = "8b";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(committedClips()[0].h3TextEncoder).toBe("8b");
    });

    it("offers ClipProj installation and exposes encoders after backend installation", async () => {
        await loadCatalog(minimaxCatalog());
        const installFeatureById = jest.fn();
        Object.assign(globalThis, { installFeatureById });
        h.setup([{ duration: 4, stages: [{}] }]);
        const install = h
            .section("prompt")
            .querySelector<HTMLButtonElement>("[data-vst-install-clipproj]");
        expect(install?.textContent).toBe("Install ComfyUI-ClipProj");
        expect(
            h
                .section("prompt")
                .querySelector("select[data-vst-h3-text-encoder]"),
        ).toBeNull();
        install?.click();
        expect(installFeatureById).toHaveBeenCalledWith(
            H3_TEXT_ENCODER_FEATURE,
            expect.any(String),
        );
        backend.currentBackendFeatureSet = [H3_TEXT_ENCODER_FEATURE];
        h.editor.refresh();
        const select = h.field<HTMLSelectElement>(
            "prompt",
            "Text Encoder",
            "select",
        );
        expect(select.value).toBe("default");
        select.value = "4b";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(committedClips()[0].h3TextEncoder).toBe("4b");
        await loadCatalog(testArchitectureCatalog());
        h.editor.refresh();
        expect(
            h.field<HTMLSelectElement>("prompt", "Reference resize", "select")
                .value,
        ).toBe("crop");
        expect(
            h.section("prompt").querySelector("[data-vst-h3-text-encoder]"),
        ).toBeNull();
        expect(
            h.section("prompt").querySelector("[data-vst-install-clipproj]"),
        ).toBeNull();
    });
});
