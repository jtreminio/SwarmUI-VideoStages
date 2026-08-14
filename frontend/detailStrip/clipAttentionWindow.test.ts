import { afterEach, describe, expect, it, jest } from "@jest/globals";
import {
    testArchitectureCatalog,
    testAuthoringTransactionSnapshot,
} from "../__test_helpers__/architectureFixtures";
import { minimalClip } from "../__test_helpers__/clipFixtures";
import type { ArchitectureModelCatalog } from "../architectures/types";
import { H3_TEXT_ENCODER_FEATURE } from "../generatedMiniMaxTextEncoder";
import { H3_ATTENTION_WINDOW_FEATURE } from "../h3AttentionWindow";
import { setVideoStagesHostBridgeForTests } from "../host";
import { __resetPersistenceForTests } from "../persistence/repository";
import type { AuthoringDocument, Clip } from "../types";
import { buildClipBody } from "./clipPanel";
import type { DetailStripContext } from "./context";

const minimaxCatalog = (): ArchitectureModelCatalog => {
    const catalog = testArchitectureCatalog();
    catalog.architectures[0].id = "minimax";
    catalog.architectures[0].label = "MiniMax H3";
    for (const entry of catalog.entries) {
        entry.architectureId = "minimax";
        entry.modelProfileId = "minimax-h3";
        entry.modelClassId = "minimax-h3";
    }
    return catalog;
};

const context = (
    catalog: ArchitectureModelCatalog,
    clips: Clip[],
): DetailStripContext =>
    ({
        commit: (mutate: (clips: Clip[]) => void) => mutate(clips),
        commitState: jest.fn(),
        debouncedCommit: (_key: string, mutate: (clips: Clip[]) => void) =>
            mutate(clips),
        debouncedCommitState: jest.fn(),
        buildClampedNumber: () => document.createElement("input"),
        structuralCommit: jest.fn(),
        render: jest.fn(),
        addRefEntry: jest.fn(),
        deleteRefEntry: jest.fn(),
        addClipReference: jest.fn(),
        deleteClipReference: jest.fn(),
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
        getSettingsMode: () => null,
        setSettingsMode: jest.fn(),
        authoring: () => testAuthoringTransactionSnapshot(catalog),
    }) as DetailStripContext;

const documentFor = (clips: Clip[]): AuthoringDocument => ({
    width: 512,
    height: 512,
    fps: 24,
    dimsExplicit: false,
    clips,
    audioTracks: [],
});

afterEach(() => {
    delete (globalThis as { currentBackendFeatureSet?: string[] })
        .currentBackendFeatureSet;
    setVideoStagesHostBridgeForTests(null);
    Reflect.deleteProperty(globalThis, "installFeatureById");
    __resetPersistenceForTests();
    document.body.innerHTML = "";
});

describe("MiniMax H3 attention window", () => {
    it("renders below clip basics and commits seconds when JuanAttn is installed", () => {
        (
            globalThis as { currentBackendFeatureSet?: string[] }
        ).currentBackendFeatureSet = [H3_ATTENTION_WINDOW_FEATURE];
        const clip = minimalClip();
        const clips: Clip[] = [clip];

        const body = buildClipBody(
            context(minimaxCatalog(), clips),
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            documentFor(clips),
        );

        const labels = Array.from(
            body.querySelectorAll<HTMLElement>(".auto-input-name"),
        ).map((label) => label.textContent);
        expect(labels.indexOf("Attention window (s)")).toBeGreaterThan(
            labels.indexOf("Reference resize"),
        );
        const number = body.querySelector<HTMLInputElement>(
            "[data-vst-h3-attention-window] input.auto-slider-number",
        );
        expect(number?.value).toBe("0");
        if (!number) throw new Error("attention window slider missing");
        number.value = "2.5";
        number.dispatchEvent(new Event("input", { bubbles: true }));

        expect(clip.h3AttentionWindowSeconds).toBe(2.5);
    });

    it("stays hidden unless JuanAttn is installed and the clip uses MiniMax H3", () => {
        const clip = minimalClip();
        const clips = [clip];
        const withoutJuanAttn = buildClipBody(
            context(minimaxCatalog(), clips),
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            documentFor(clips),
        );
        expect(
            withoutJuanAttn.querySelector("[data-vst-h3-attention-window]"),
        ).toBeNull();

        (
            globalThis as { currentBackendFeatureSet?: string[] }
        ).currentBackendFeatureSet = [H3_ATTENTION_WINDOW_FEATURE];
        const wrongArchitecture = buildClipBody(
            context(testArchitectureCatalog(), clips),
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            documentFor(clips),
        );
        expect(
            wrongArchitecture.querySelector("[data-vst-h3-attention-window]"),
        ).toBeNull();
    });
});

describe("MiniMax H3 text encoder", () => {
    it("renders below attention window and commits the selected encoder", () => {
        (
            globalThis as { currentBackendFeatureSet?: string[] }
        ).currentBackendFeatureSet = [
            H3_ATTENTION_WINDOW_FEATURE,
            H3_TEXT_ENCODER_FEATURE,
        ];
        const clip = minimalClip();
        const clips = [clip];

        const body = buildClipBody(
            context(minimaxCatalog(), clips),
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            documentFor(clips),
        );

        const labels = Array.from(
            body.querySelectorAll<HTMLElement>(".auto-input-name"),
        ).map((label) => label.textContent);
        expect(labels.indexOf("Text Encoder")).toBeGreaterThan(
            labels.indexOf("Attention window (s)"),
        );
        const select = body.querySelector<HTMLSelectElement>(
            "select[data-vst-h3-text-encoder]",
        );
        expect(
            Array.from(select?.options ?? []).map((option) => option.value),
        ).toEqual(["default", "8b", "4b"]);
        const help = select
            ?.closest(".vst-detail-field")
            ?.querySelector<HTMLElement>(".sui-info-popover");
        expect(help?.textContent).toContain(
            "download the matching projection automatically",
        );
        const repo = help?.querySelector<HTMLAnchorElement>("a");
        expect(repo?.href).toBe(
            "https://github.com/nicolab28/ComfyUI-ClipProj",
        );
        expect(repo?.target).toBe("_blank");
        if (!select) throw new Error("text encoder select missing");
        select.value = "8b";
        select.dispatchEvent(new Event("change", { bubbles: true }));

        expect(clip.h3TextEncoder).toBe("8b");
    });

    it("offers ClipProj installation in place of the dropdown", () => {
        const clip = minimalClip();
        const clips = [clip];
        const installFeatureById = jest.fn();
        (
            globalThis as typeof globalThis & {
                installFeatureById?: typeof installFeatureById;
            }
        ).installFeatureById = installFeatureById;
        const withoutClipProj = buildClipBody(
            context(minimaxCatalog(), clips),
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            documentFor(clips),
        );
        const install = withoutClipProj.querySelector<HTMLButtonElement>(
            "[data-vst-install-clipproj]",
        );
        expect(install?.textContent).toBe("Install ComfyUI-ClipProj");
        expect(
            withoutClipProj.querySelector("select[data-vst-h3-text-encoder]"),
        ).toBeNull();
        install?.click();
        expect(installFeatureById).toHaveBeenCalledWith(
            H3_TEXT_ENCODER_FEATURE,
            expect.any(String),
        );

        (
            globalThis as { currentBackendFeatureSet?: string[] }
        ).currentBackendFeatureSet = [H3_TEXT_ENCODER_FEATURE];
        const wrongArchitecture = buildClipBody(
            context(testArchitectureCatalog(), clips),
            { kind: "clip", clipIdx: 0, stageIdx: 0 },
            documentFor(clips),
        );
        expect(
            wrongArchitecture.querySelector("[data-vst-h3-text-encoder]"),
        ).toBeNull();
        expect(
            wrongArchitecture.querySelector("[data-vst-install-clipproj]"),
        ).toBeNull();
    });
});
