import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCapabilities,
    testArchitectureCatalog,
    testRootDefaults,
} from "../__test_helpers__/architectureFixtures";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import { createCapabilityViewResolver } from "../architectures/policy";
import type { ArchitectureModelCatalog } from "../architectures/types";
import { __resetPersistenceForTests } from "../persistence";
import type { Clip } from "../types";
import { buildClipBody } from "./clipPanel";
import type { DetailStripContext } from "./context";
import { buildStageParamsColumn } from "./stagePanel";

/** LTX-2 catalog with frame references and upscaling taken away. */
const restrictedCatalog = (): ArchitectureModelCatalog => {
    const models = testArchitectureCatalog();
    models.architectures[0].capabilities = testArchitectureCapabilities({
        clip: [
            "source-video",
            "prompts",
            "prompt-relay",
            "retake",
            "audio-sources",
            "audio-segments",
        ],
        stage: ["image-input", "video-input", "lora", "ic-lora"],
        upscaleModes: [],
    });
    return models;
};

const context = (
    models: ArchitectureModelCatalog,
    clips: Clip[],
): DetailStripContext =>
    ({
        commit: (mutate: (clips: Clip[]) => void) => {
            mutate(clips);
        },
        commitState: jest.fn(),
        debouncedCommit: jest.fn(),
        debouncedCommitState: jest.fn(),
        buildClampedNumber: () => document.createElement("input"),
        structuralCommit: jest.fn(),
        render: jest.fn(),
        addRefEntry: jest.fn(),
        deleteRefEntry: jest.fn(),
        addPromptWindow: jest.fn(),
        deleteWindowEntry: jest.fn(),
        createRetake: jest.fn(),
        removeRetake: jest.fn(),
        addStage: jest.fn(),
        deleteStage: jest.fn(),
        selectStage: jest.fn(),
        toggleClipSkip: jest.fn(),
        toggleStageSkip: jest.fn(),
        getBoundBody: () => null,
        getDockEl: () => null,
        getSettingsMode: () => null,
        setSettingsMode: jest.fn(),
        capabilities: () => createCapabilityViewResolver(models),
        generatedEntryMode: () => "text-to-video",
    }) as unknown as DetailStripContext;

/** A control a user could actually reach and operate with the keyboard. */
const keyboardOperable = (element: HTMLButtonElement | null): boolean =>
    element !== null && !element.disabled && element.tabIndex >= 0;

afterEach(() => {
    jest.restoreAllMocks();
    resetArchitectureCatalogForTests();
    __resetPersistenceForTests();
    document.body.innerHTML = "";
});

describe("persisted-but-unsupported repair contract", () => {
    it("keeps the delete of an unsupported persisted reference operable", () => {
        const models = restrictedCatalog();
        const clip = minimalClip({ refs: [minimalRef()] });
        const ctx = context(models, [clip]);

        const body = buildClipBody(
            ctx,
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            [clip],
        );
        const section = body.querySelector<HTMLElement>(
            ".vst-detail-ref-section",
        );
        expect(section).not.toBeNull();
        // Visible and read-only, with the reason.
        expect(
            section?.querySelector("[data-vst-capability-unsupported]"),
        ).not.toBeNull();
        expect(
            section?.querySelector<HTMLButtonElement>(".vst-detail-add-ref")
                ?.disabled,
        ).toBe(true);

        const remove =
            section?.querySelector<HTMLButtonElement>(".vst-detail-delete");
        expect(keyboardOperable(remove ?? null)).toBe(true);
        remove?.click();
        expect(ctx.deleteRefEntry).toHaveBeenCalledWith(0, 0);
    });

    it("offers a reset for an unsupported persisted upscale", () => {
        const models = restrictedCatalog();
        const stage = minimalStage({ upscale: 2 });
        const clip = minimalClip({ stages: [minimalStage(), stage] });
        const clips = [clip];
        const ctx = context(models, clips);

        const column = buildStageParamsColumn(
            ctx,
            clip,
            0,
            1,
            stage,
            testRootDefaults(models),
        );

        const reset = column.querySelector<HTMLButtonElement>(
            ".vst-reset-unsupported-upscale",
        );
        expect(keyboardOperable(reset)).toBe(true);
        reset?.click();
        expect(clips[0].stages[1].upscale).toBe(1);
    });

    it("keeps unsupported green framing visible with an operable reset", () => {
        const models = restrictedCatalog();
        const clip = minimalClip({ refFraming: "fit-green" });
        const clips = [clip];
        const ctx = context(models, clips);

        const body = buildClipBody(
            ctx,
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            clips,
        );

        const select = body.querySelector<HTMLSelectElement>(
            "[data-vst-reference-framing]",
        );
        expect(select?.value).toBe("fit-green");
        expect(select?.disabled).toBe(true);
        expect(body.querySelector(".sui-popover")?.textContent).toContain(
            "#66FF00",
        );
        const reset = body.querySelector<HTMLButtonElement>(
            ".vst-reset-unsupported-reference-framing",
        );
        expect(keyboardOperable(reset)).toBe(true);
        reset?.click();
        expect(clips[0].refFraming).toBe("crop");
    });

    it("commits a supported clip-level reference framing selection", () => {
        const models = testArchitectureCatalog();
        const clip = minimalClip();
        const clips = [clip];
        const ctx = context(models, clips);
        const body = buildClipBody(
            ctx,
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            clips,
        );
        const select = body.querySelector<HTMLSelectElement>(
            "[data-vst-reference-framing]",
        );

        expect(select?.disabled).toBe(false);
        if (!select) throw new Error("reference framing select missing");
        select.value = "fit";
        select.dispatchEvent(new Event("change", { bubbles: true }));

        expect(clips[0].refFraming).toBe("fit");
    });
});
