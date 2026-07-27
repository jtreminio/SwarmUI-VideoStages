import { describe, expect, it, jest } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
} from "../__test_helpers__/architectureFixtures";
import {
    hdrIcLoraFixture,
    minimalClip,
} from "../__test_helpers__/clipFixtures";
import {
    mountPromptBox,
    mountSelect,
    mountVideoStagesData,
} from "../__test_helpers__/dom";
import { createCapabilityViewResolver } from "../architectures/policy";
import {
    __resetPersistenceForTests,
    dispatchDocumentCommand,
    getClips,
    getTimelineStore,
    saveClips,
} from "../persistence";
import { getSelection, setSelection } from "../selection";
import { createTimelineHistory } from "../timelineHistory";
import type { Clip, TimelineSelection } from "../types";
import type { StructuralCommand } from "./draftQueue";
import { applyClipSkip, applyStageSkip } from "./selectionDomainOperations";
import { createDetailSelectionOperations } from "./selectionOperations";

type StructuralOutcome =
    | TimelineSelection
    | "render"
    | null
    | StructuralCommand;

describe("detail structural stage operations", () => {
    it("allows legacy skipped first items to be restored, but not skipped again", () => {
        const clip = minimalClip({ skipped: true });
        clip.stages[0].skipped = true;
        const clips = [clip];
        const catalog = testArchitectureCatalog();

        expect(applyClipSkip(clips, 0, "text-to-video")).toBe(true);
        expect(clips[0].skipped).toBe(false);
        expect(applyClipSkip(clips, 0, "text-to-video")).toBe(false);
        expect(applyStageSkip(clips, 0, 0, catalog, "text-to-video")).toBe(
            true,
        );
        expect(clips[0].stages[0].skipped).toBe(false);
        expect(applyStageSkip(clips, 0, 0, catalog, "text-to-video")).toBe(
            false,
        );
    });

    it("does not remove the first stage, even from a sourced clip", () => {
        const clips: Clip[] = [
            minimalClip({
                sourceVideo: {
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
            () => createCapabilityViewResolver(testArchitectureCatalog()),
        );

        operations.deleteStage(0, 0);

        expect(clips[0].stages).toHaveLength(1);
        expect(clips[0]).toMatchObject({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
        });
        expect(selection).toBeNull();
    });

    it("does not add a later stage when the architecture lacks multi-stage support", () => {
        const clips = [
            minimalClip({
                architecture: "test-video",
                modelProfileId: "test-profile",
            }),
        ];
        const models = fakeArchitectureCatalog();
        models.architectures[0].capabilities.architecture =
            models.architectures[0].capabilities.architecture.filter(
                (feature) => feature !== "multi-stage",
            );
        const structuralCommit = jest.fn(
            (apply: (value: Clip[]) => StructuralOutcome) => {
                apply(clips);
            },
        );
        const operations = createDetailSelectionOperations(
            structuralCommit,
            () => createCapabilityViewResolver(models),
        );

        operations.addStage(0);

        expect(clips[0].stages).toHaveLength(1);
    });

    it("derives an empty generated clip identity through the shared reconciler when adding its first stage", () => {
        document.body.innerHTML = "";
        mountSelect("input_videomodel", {
            value: "ltx-2.3.safetensors",
            options: ["ltx-2.3.safetensors"],
        });
        const clips = [
            minimalClip({
                architecture: "ltx2",
                modelProfileId: "stale-profile",
                loras: [{ name: "detail.safetensors" }],
                icLoras: [hdrIcLoraFixture()],
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
            () => createCapabilityViewResolver(testArchitectureCatalog()),
        );

        operations.addStage(0);

        expect(clips[0]).toMatchObject({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
        });
        expect(clips[0].stages).toHaveLength(1);
        expect(clips[0].stages[0]).toMatchObject({
            model: "ltx-2.3.safetensors",
            modelProfileId: "ltx-2.3",
            loraWeights: [1],
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
                    architecture: "none",
                    modelProfileId: "none",
                    stages: [],
                    sourceVideo: {
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
            () => createCapabilityViewResolver(testArchitectureCatalog()),
        );

        operations.addStage(0);

        const state = getTimelineStore().getSnapshot();
        expect(state.revision).toBe(revisionBefore + 1);
        expect(notifications).toEqual(["detail-strip"]);
        expect(state.state.clips[0]).toMatchObject({
            architecture: "ltx2",
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
