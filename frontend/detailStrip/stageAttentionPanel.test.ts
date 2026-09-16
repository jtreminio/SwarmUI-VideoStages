import { describe, expect, it } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCatalog,
    testArchitectureCatalogDto,
} from "../__test_helpers__/architectureFixtures";
import { mountSelect } from "../__test_helpers__/dom";
import { committedClips, editorHarness } from "../__test_helpers__/editor";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import {
    decodeStoredDocument,
    serializeClipsForStorage,
} from "../persistence/documentCodec";
import { getDefaultStageModel, getRootDefaults } from "../rootDefaults";
import { CURRENT_AUTHORING_SCHEMA_VERSION } from "../types";

describe("stage attention", () => {
    const h = editorHarness();

    it.each([
        [
            "Model attention backend",
            "modelattentionbackend",
            "modelAttentionBackend",
            "modelAttentionBackendEnabled",
            ["pytorch attention", "comfy kitchen attention"],
        ],
        [
            "Use sparse attention",
            "usesparseattention",
            "useSparseAttention",
            "useSparseAttentionEnabled",
            ["None", "sol", "topk", "vsa"],
        ],
    ] as const)("toggles, persists and copies %s independently per stage", async (label, paramId, key, enabledKey, options) => {
        const catalog = testArchitectureCatalog();
        catalog.architectures[0].id = "wan";
        catalog.architectures[0].capabilities.features.push("stageAttention");
        for (const entry of catalog.entries) entry.architectureId = "wan";
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(catalog),
        });
        await loadAuthoritativeArchitectureCatalog();
        h.setup([{ duration: 4, stages: [{}, {}] }]);
        mountSelect(`input_${paramId}`, { options: [...options] });
        h.editor.refresh();
        const select = () =>
            h.field<HTMLSelectElement>("stages", label, "select", 0);
        const toggle = () =>
            h.field<HTMLInputElement>(
                "stages",
                label,
                "input[type=checkbox]",
                0,
            );
        expect(
            select().closest(".vst-stage-attention")?.querySelector("h5")
                ?.textContent,
        ).toBe("Attention");
        expect([...select().options].map((option) => option.value)).toEqual(
            options,
        );
        expect(select().disabled).toBe(false);
        expect(toggle().checked).toBe(false);
        select().focus();
        expect(toggle().checked).toBe(true);
        expect(committedClips()[0].stages[0][enabledKey]).toBe(true);
        toggle().focus();
        toggle().click();
        expect(toggle().checked).toBe(false);
        expect(select().disabled).toBe(false);
        select().value = options[1];
        select().dispatchEvent(new Event("change", { bubbles: true }));
        expect(toggle().checked).toBe(true);
        expect(committedClips()[0].stages[0][key]).toBe(options[1]);
        expect(committedClips()[0].stages[0][enabledKey]).toBe(true);
        expect(committedClips()[0].stages[1][enabledKey]).toBeUndefined();
        toggle().focus();
        toggle().click();
        h.editor.refresh();
        expect(select().disabled).toBe(false);
        expect(select().value).toBe(options[1]);
        expect(toggle().checked).toBe(false);
        expect(committedClips()[0].stages[0][enabledKey]).toBe(false);
        toggle().focus();
        toggle().click();
        const secondToggle = h.field<HTMLInputElement>(
            "stages",
            label,
            "input[type=checkbox]",
            1,
        );
        secondToggle.click();
        const second = h.field<HTMLSelectElement>("stages", label, "select", 1);
        second.value = options[options.length - 1];
        second.dispatchEvent(new Event("change", { bubbles: true }));
        h.section("stages")
            .querySelector<HTMLButtonElement>("[data-testid=add-stages]")
            ?.click();
        expect(committedClips()[0].stages[2][key]).toBe(
            options[options.length - 1],
        );
        expect(committedClips()[0].stages[2][enabledKey]).toBe(true);
        const defaults = getRootDefaults();
        const restored = decodeStoredDocument(
            JSON.stringify({
                schemaVersion: CURRENT_AUTHORING_SCHEMA_VERSION,
                clips: serializeClipsForStorage(committedClips()),
            }),
            { width: 1024, height: 1024, fps: 24 },
            defaults,
            getDefaultStageModel(defaults),
        );
        expect(
            restored?.clips[0].stages.map((stage) => [
                stage[key],
                stage[enabledKey],
            ]),
        ).toEqual([
            [options[1], true],
            [options[options.length - 1], true],
            [options[options.length - 1], true],
        ]);
    });
});
