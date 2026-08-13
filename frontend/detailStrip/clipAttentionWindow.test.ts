import { afterEach, describe, expect, it, jest } from "@jest/globals";
import {
    testArchitectureCatalog,
    testAuthoringTransactionSnapshot,
} from "../__test_helpers__/architectureFixtures";
import { minimalClip } from "../__test_helpers__/clipFixtures";
import type { ArchitectureModelCatalog } from "../architectures/types";
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
