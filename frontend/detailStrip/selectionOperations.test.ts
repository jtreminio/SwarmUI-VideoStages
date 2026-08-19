import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCatalogDto,
    testAuthoringTransactionSnapshot,
} from "../__test_helpers__/architectureFixtures";
import { icLoraFixture, minimalClip } from "../__test_helpers__/clipFixtures";
import {
    mountPromptBox,
    mountSelect,
    mountVideoStagesData,
} from "../__test_helpers__/dom";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import {
    __resetPersistenceForTests,
    dispatchDocumentCommand,
    getClips,
    getTimelineStore,
    saveClips,
} from "../persistence/repository";
import { getSelection, setSelection } from "../selection";
import { createTimelineHistory } from "../timelineHistory";
import type { Clip, TimelineSelection } from "../types";
import type { StructuralCommand } from "./draftQueue";
import { createDetailSelectionOperations } from "./selectionOperations";

type StructuralOutcome =
    | TimelineSelection
    | "render"
    | null
    | StructuralCommand;

describe("detail structural stage operations", () => {
    beforeEach(async () => {
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(),
        });
        await loadAuthoritativeArchitectureCatalog();
    });

    afterEach(() => {
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests(null);
    });

    it("emits stable-ID commands for clip and stage skip toggles", () => {
        const clip = minimalClip({
            id: "clip-a",
            stages: [
                { ...minimalClip().stages[0], id: "stage-a" },
                { ...minimalClip().stages[0], id: "stage-b" },
            ],
        });
        const outcomes: StructuralOutcome[] = [];
        const structuralCommit = jest.fn(
            (apply: (value: Clip[]) => StructuralOutcome) => {
                outcomes.push(apply([clip]));
            },
        );
        const captureTransaction = jest.fn(() =>
            testAuthoringTransactionSnapshot(),
        );
        const operations = createDetailSelectionOperations(
            structuralCommit,
            captureTransaction,
        );

        operations.toggleClipSkip(0);
        operations.toggleStageSkip(0, 1);

        expect(outcomes).toEqual([
            {
                command: { type: "clip.toggle-skip", clipId: "clip-a" },
                selection: "render",
            },
            {
                command: {
                    type: "stage.toggle-skip",
                    clipId: "clip-a",
                    stageId: "stage-b",
                },
                selection: "render",
            },
        ]);
        expect(captureTransaction).not.toHaveBeenCalled();
    });

    it("keeps a newly appended stage behind an existing skip marker", () => {
        const clip = minimalClip({
            stages: [
                minimalClip().stages[0],
                {
                    ...structuredClone(minimalClip().stages[0]),
                    skipped: true,
                },
            ],
        });
        const structuralCommit = jest.fn(
            (apply: (value: Clip[]) => StructuralOutcome) => {
                apply([clip]);
            },
        );
        const captureTransaction = jest.fn(() =>
            testAuthoringTransactionSnapshot(),
        );
        const operations = createDetailSelectionOperations(
            structuralCommit,
            captureTransaction,
        );

        operations.addStage(0);

        expect(clip.stages).toHaveLength(3);
        expect(clip.stages[2].skipped).toBe(true);
        expect(captureTransaction).toHaveBeenCalledTimes(1);
    });

    it("does not remove the first stage, even from a init-video clip", () => {
        const clips: Clip[] = [
            minimalClip({
                initVideo: {
                    data: "data:video/mp4;base64,AA==",
                    fileName: "source.mp4",
                    fps: 24,
                    durationSeconds: 2,
                    startSeconds: 0,
                    lengthSeconds: 2,
                },
            }),
        ];
        let selection: StructuralOutcome = null;
        const structuralCommit = jest.fn(
            (apply: (value: Clip[]) => StructuralOutcome) => {
                selection = apply(clips);
            },
        );
        const operations = createDetailSelectionOperations(
            structuralCommit,
            () => testAuthoringTransactionSnapshot(),
        );

        operations.deleteStage(0, 0);

        expect(clips[0].stages).toHaveLength(1);
        expect(clips[0]).toMatchObject({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
        });
        expect(selection).toBeNull();
    });

    it("derives an empty generated clip identity through the shared reconciler when adding its first stage", () => {
        document.body.innerHTML = "";
        mountSelect("input_videomodel", {
            value: "ltx-2.3.safetensors",
            options: ["ltx-2.3.safetensors"],
        });
        const clips = [
            minimalClip({
                architectureHint: "ltx2",
                modelProfileId: "stale-profile",
                loras: [{ name: "detail.safetensors", weight: 1 }],
                icLoras: [icLoraFixture()],
                stages: [],
            }),
        ];
        let selection: StructuralOutcome = null;
        const structuralCommit = jest.fn(
            (apply: (value: Clip[]) => StructuralOutcome) => {
                selection = apply(clips);
            },
        );
        const operations = createDetailSelectionOperations(
            structuralCommit,
            () => testAuthoringTransactionSnapshot(),
        );

        operations.addStage(0);

        expect(clips[0]).toMatchObject({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
        });
        expect(clips[0].stages).toHaveLength(1);
        expect(clips[0].stages[0]).toMatchObject({
            model: "ltx-2.3.safetensors",
            modelProfileId: "ltx-2.3",
            icLoraStrengths: [1],
        });
        expect(selection).toEqual({ kind: "clip", clipIdx: 0, stageIdx: 0 });
    });

    it("adds the first generated stage to a source-only clip as one named atomic batch", () => {
        document.body.innerHTML = "";
        __resetPersistenceForTests();
        mountVideoStagesData({
            clips: [
                minimalClip({
                    architectureHint: "none",
                    modelProfileId: "none",
                    stages: [],
                    initVideo: {
                        data: "data:video/mp4;base64,AA==",
                        fileName: "source.mp4",
                        fps: 24,
                        durationSeconds: 2,
                        startSeconds: 0,
                        lengthSeconds: 2,
                    },
                }),
            ],
            audioTracks: [],
        });
        mountPromptBox("");
        mountSelect("input_videomodel", {
            value: "ltx-2.3.safetensors",
            options: ["ltx-2.3.safetensors"],
        });
        const notifications: string[] = [];
        const carrier = document.getElementById(
            "input_videostages",
        ) as HTMLTextAreaElement;
        const beforeCarrier = carrier.value;
        const history = createTimelineHistory({
            read: () => carrier.value,
            write: (value) => {
                carrier.value = value;
            },
        });
        history.rebase();
        getTimelineStore().subscribe((_state, meta) => {
            notifications.push(meta.origin);
            history.capture();
        });
        const revisionBefore = getTimelineStore().getSnapshot().revision;
        // Stands in for the draft queue: a named command dispatches, anything
        // else falls back to the whole-document save path.
        const structuralCommit = (
            apply: (value: Clip[]) => StructuralOutcome,
        ): void => {
            const clips = getClips();
            const outcome = apply(clips);
            if (outcome === null) {
                return;
            }
            if (typeof outcome === "object" && "command" in outcome) {
                const result = dispatchDocumentCommand(outcome.command, {
                    origin: "detail-strip",
                    notifyDomChange: false,
                });
                if (result.applied && outcome.selection !== null) {
                    setSelection(outcome.selection as TimelineSelection);
                }
                return;
            }
            saveClips(clips, {
                origin: "detail-strip",
                notifyDomChange: false,
            });
        };
        const operations = createDetailSelectionOperations(
            structuralCommit,
            () => testAuthoringTransactionSnapshot(),
        );

        operations.addStage(0);

        const state = getTimelineStore().getSnapshot();
        expect(state.revision).toBe(revisionBefore + 1);
        expect(notifications).toEqual(["detail-strip"]);
        expect(state.state.clips[0]).toMatchObject({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
        });
        expect(state.state.clips[0].stages).toHaveLength(1);
        expect(state.state.clips[0].stages[0]).toMatchObject({
            model: "ltx-2.3.safetensors",
            modelProfileId: "ltx-2.3",
        });
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 0,
            stageIdx: 0,
        });
        const afterCarrier = carrier.value;
        expect(history.undo()).toBe(true);
        expect(carrier.value).toBe(beforeCarrier);
        expect(history.undo()).toBe(false);
        expect(history.redo()).toBe(true);
        expect(carrier.value).toBe(afterCarrier);
        expect(history.redo()).toBe(false);
    });
});
