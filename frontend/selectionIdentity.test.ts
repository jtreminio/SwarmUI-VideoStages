import { beforeEach, describe, expect, it } from "@jest/globals";

import { minimalClip, minimalRef } from "./__test_helpers__/clipFixtures";
import {
    mountPromptBox,
    mountSelect,
    mountVideoFps,
    mountVideoStagesData,
} from "./__test_helpers__/dom";
import { defaultIcLora } from "./architectures/ltx2/icLoraNormalization";
import {
    __resetPersistenceForTests,
    getClips,
    getState,
    saveClips,
} from "./persistence/repository";
import {
    getSelection,
    resetSelectionForTests,
    setSelection,
} from "./selection";
import { createTimelineHistory } from "./timelineHistory";

const mountDocument = (): void => {
    document.body.innerHTML = "";
    __resetPersistenceForTests();
    resetSelectionForTests();
    mountVideoFps(24);
    mountPromptBox("");
    mountSelect("input_videomodel", {
        value: "ltx-2.3.safetensors",
        options: ["ltx-2.3.safetensors"],
    });
    mountVideoStagesData({
        clips: [
            minimalClip({ id: "clip-a", duration: 3 }),
            minimalClip({
                id: "clip-b",
                duration: 3,
                refs: [
                    minimalRef({ id: "ref-b0", frame: 0 }),
                    minimalRef({ id: "ref-b1", frame: 5 }),
                ],
                icLoras: [
                    defaultIcLora({ id: "ic-a", lora: "A" }),
                    defaultIcLora({ id: "ic-b", lora: "B" }),
                    defaultIcLora({ id: "ic-c", lora: "C" }),
                ],
            }),
        ],
        audioTracks: [],
    });
    // Force the first decode so the store holds the mounted document.
    getState();
};

describe("identity-anchored selection", () => {
    beforeEach(mountDocument);

    it("keeps a ref selection on the same ref after an earlier clip is deleted", () => {
        setSelection({ kind: "ref", clipIdx: 1, refIdx: 1 });
        expect(getState().clips[1].refs[1].id).toBe("ref-b1");

        const clips = getClips();
        clips.splice(0, 1);
        saveClips(clips, { notifyDomChange: false });

        const selection = getSelection();
        expect(selection).toEqual({ kind: "ref", clipIdx: 0, refIdx: 1 });
        if (selection.kind !== "ref") {
            throw new Error("expected a ref selection");
        }
        expect(
            getState().clips[selection.clipIdx].refs[selection.refIdx].id,
        ).toBe("ref-b1");
    });

    it("keeps a relay-window selection on the same window after an earlier clip is deleted", () => {
        const seeded = getClips();
        seeded[1].promptWindows = [
            { prompt: "first", start: 0, duration: 1 },
            { prompt: "second", start: 1, duration: 1 },
        ];
        saveClips(seeded, { notifyDomChange: false });
        const windowId = getState().clips[1].promptWindows[1].id;
        expect(windowId).toBeTruthy();

        setSelection({ kind: "prompt-minor", clipIdx: 1, windowIdx: 1 });

        const clips = getClips();
        clips.splice(0, 1);
        saveClips(clips, { notifyDomChange: false });

        const selection = getSelection();
        expect(selection).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 1,
        });
        if (selection.kind !== "prompt-minor") {
            throw new Error("expected a relay-window selection");
        }
        expect(
            getState().clips[selection.clipIdx].promptWindows[
                selection.windowIdx
            ].id,
        ).toBe(windowId);
    });

    it("keeps an IC-LoRA selection on the same entry after an earlier entry is removed", () => {
        setSelection({ kind: "ic-lora", clipIdx: 1, entryIdx: 2 });

        const clips = getClips();
        clips[1].icLoras.splice(0, 1);
        saveClips(clips, { notifyDomChange: false });

        const selection = getSelection();
        expect(selection).toEqual({
            kind: "ic-lora",
            clipIdx: 1,
            entryIdx: 1,
        });
        if (selection.kind !== "ic-lora") {
            throw new Error("expected an IC-LoRA selection");
        }
        expect(
            getState().clips[selection.clipIdx].icLoras[selection.entryIdx].id,
        ).toBe("ic-c");
    });

    it("degrades a removed IC-LoRA selection to its nearest sibling, then its clip", () => {
        setSelection({ kind: "ic-lora", clipIdx: 1, entryIdx: 1 });

        const clips = getClips();
        clips[1].icLoras.splice(1, 1);
        saveClips(clips, { notifyDomChange: false });

        expect(getSelection()).toEqual({
            kind: "ic-lora",
            clipIdx: 1,
            entryIdx: 1,
        });
        expect(getState().clips[1].icLoras[1].id).toBe("ic-c");

        const remaining = getClips();
        remaining[1].icLoras.splice(0);
        saveClips(remaining, { notifyDomChange: false });

        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 1,
            stageIdx: 0,
        });
    });

    it("re-points a nested selection at the same entity across an undo/redo cycle", () => {
        const carrier = document.getElementById(
            "input_videostages",
        ) as HTMLTextAreaElement;
        const history = createTimelineHistory({
            read: () => carrier.value,
            write: (value) => {
                carrier.value = value;
            },
        });
        history.rebase();

        setSelection({ kind: "ref", clipIdx: 1, refIdx: 1 });

        const clips = getClips();
        clips.splice(0, 1);
        saveClips(clips, { notifyDomChange: false });
        history.capture();
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 1 });

        expect(history.undo()).toBe(true);
        __resetPersistenceForTests();
        expect(getState().clips.map((clip) => clip.id)).toEqual([
            "clip-a",
            "clip-b",
        ]);
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 1, refIdx: 1 });

        expect(history.redo()).toBe(true);
        __resetPersistenceForTests();
        expect(getState().clips.map((clip) => clip.id)).toEqual(["clip-b"]);
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 1 });
    });

    it("degrades to the nearest surviving sibling when the selected entity is gone", () => {
        setSelection({ kind: "ref", clipIdx: 1, refIdx: 1 });

        const clips = getClips();
        clips[1].refs.splice(1, 1);
        saveClips(clips, { notifyDomChange: false });

        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 1, refIdx: 0 });

        const remaining = getClips();
        remaining[1].refs.splice(0, 1);
        saveClips(remaining, { notifyDomChange: false });

        expect(getSelection()).toEqual({ kind: "none" });
    });
});
