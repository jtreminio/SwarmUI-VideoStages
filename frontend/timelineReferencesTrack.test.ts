import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { resetArchitectureCatalogForTests } from "./__test_helpers__/architectureCatalog";
import {
    testArchitectureCatalog,
    testArchitectureCatalogDto,
    testAuthoringTransactionSnapshot,
} from "./__test_helpers__/architectureFixtures";
import {
    lastSavedClips,
    mountPromptBox,
    mountSelect,
    mountTimelineBody,
    mountVideoFps,
    mountVideoStagesData,
    stubRect,
    TIMELINE_PPS,
} from "./__test_helpers__/dom";
import { loadAuthoritativeArchitectureCatalog } from "./architectures/catalog";
import type {
    ArchitectureModelCatalog,
    FrameReferencePosition,
} from "./architectures/types";
import { createGestureRouter } from "./gestureRouter";
import { setVideoStagesHostBridgeForTests } from "./host";
import { createDefaultVideoStagesHostBridge } from "./host/defaultVideoStagesHostBridge";
import * as persistence from "./persistence/repository";
import {
    getSelection,
    resetSelectionForTests,
    setSelection,
    subscribeSelection,
} from "./selection";
import {
    createTimelineReferencesTrack,
    type TimelineReferencesTrack,
} from "./timelineReferencesTrack";
import { renderTimeline } from "./timelineView";
import { computeRegionLayout } from "./timelineView/layout";
import { renderReferencesTrackRow } from "./timelineView/trackRows";
import type { Clip } from "./types";

interface RefFixture {
    source?: string;
    frame?: number;
    fromEnd?: boolean;
}

interface ClipFixture {
    duration: number;
    frameRefs?: RefFixture[];
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    audioSource: "Native",
    stages: [{ model: "ltx-2.3.safetensors" }],
    frameRefs: (clip.frameRefs ?? []).map((ref) => ({
        source: ref.source ?? "Refiner",
        frame: ref.frame ?? 1,
        fromEnd: ref.fromEnd ?? false,
    })),
    promptWindows: [],
});

const mountPrompt = (clips: ClipFixture[], fps?: number): void => {
    mountPromptBox("");
    if (fps !== undefined) {
        mountVideoFps(fps);
    }
    mountVideoStagesData({ clips: clips.map(clipRecord) });
};

const renderRefs = (body: HTMLElement, clips: Clip[]): void => {
    const layouts = computeRegionLayout(clips, { pxPerSecond: TIMELINE_PPS });
    body.innerHTML = renderReferencesTrackRow(clips, layouts, 24, "seconds");
};

describe("createTimelineReferencesTrack (selection + gestures)", () => {
    let track: TimelineReferencesTrack | null = null;
    let router: ReturnType<typeof createGestureRouter> | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;
    let authoringCatalog: ArchitectureModelCatalog;

    beforeEach(() => {
        resetSelectionForTests();
        localStorage.clear();
        saveSpy = jest
            .spyOn(persistence, "saveClips")
            .mockImplementation(() => {});
    });

    afterEach(() => {
        track?.dispose();
        track = null;
        router?.dispose();
        router = null;
        jest.restoreAllMocks();
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests(null);
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    const setup = (
        fixtures: ClipFixture[],
        fps?: number,
        referencePositions: FrameReferencePosition[] = ["any"],
    ): HTMLElement => {
        mountPrompt(fixtures, fps);
        const body = mountTimelineBody();
        renderRefs(body, persistence.getClips());
        authoringCatalog = testArchitectureCatalog();
        for (const entry of authoringCatalog.entries) {
            entry.enhancements = { referencePositions };
        }
        track = createTimelineReferencesTrack(() =>
            testAuthoringTransactionSnapshot(authoringCatalog),
        );
        router = createGestureRouter();
        track.attach(body, router);
        router.attach(body);
        return body;
    };

    const markEl = (
        body: HTMLElement,
        clipIdx: number,
        refIdx: number,
    ): HTMLElement => {
        const el = body.querySelector<HTMLElement>(
            `.vst-refs-mark[data-clip-idx="${clipIdx}"][data-ref-idx="${refIdx}"]`,
        );
        if (!el) {
            throw new Error(
                `ref thumb not found: clip ${clipIdx} ref ${refIdx}`,
            );
        }
        return el;
    };

    const stubLaneRect = (
        body: HTMLElement,
        clipIdx: number,
        left: number,
        width: number,
    ): void => {
        const lane = body.querySelector<HTMLElement>(
            `.vst-refs-lane[data-clip-idx="${clipIdx}"]`,
        );
        if (!lane) {
            throw new Error(`ref lane not found: clip ${clipIdx}`);
        }
        stubRect(lane, left, width);
    };

    const dragThumb = (
        thumb: HTMLElement,
        fromX: number,
        toX: number,
    ): void => {
        thumb.dispatchEvent(
            new MouseEvent("mousedown", {
                bubbles: true,
                button: 0,
                clientX: fromX,
            }),
        );
        document.dispatchEvent(
            new MouseEvent("mousemove", { bubbles: true, clientX: toX }),
        );
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: toX }),
        );
    };

    it("selects the reference when its thumb is clicked", () => {
        const body = setup([
            { duration: 5, frameRefs: [{ source: "Refiner" }] },
        ]);
        markEl(body, 0, 0).dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 0 });
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("re-activates an already-selected reference when its thumb is clicked", () => {
        const body = setup([
            { duration: 5, frameRefs: [{ source: "Refiner" }] },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        const observed: unknown[] = [];
        const stop = subscribeSelection((selection) =>
            observed.push(selection),
        );
        markEl(body, 0, 0).dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );
        stop();
        expect(observed).toEqual([{ kind: "ref", clipIdx: 0, refIdx: 0 }]);
    });

    it("selects the reference via keyboard activation", () => {
        const body = setup([
            { duration: 5, frameRefs: [{ source: "Refiner" }] },
        ]);
        markEl(body, 0, 0).dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
        );
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 0 });
    });

    it("deletes the reference (and its stage ref-strengths) via shift+click", () => {
        const body = setup([
            {
                duration: 5,
                frameRefs: [{ source: "Refiner" }, { source: "Base" }],
            },
        ]);
        markEl(body, 0, 0).dispatchEvent(
            new MouseEvent("click", { bubbles: true, shiftKey: true }),
        );

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const clips = lastSavedClips<Clip[]>(saveSpy);
        expect(clips[0].frameRefs).toHaveLength(1);
        expect(clips[0].frameRefs[0].source).toBe("Base");
        expect(clips[0].stages[0].frameRefStrengths).toHaveLength(1);
        expect(getSelection().kind).toBe("none");
    });

    it("adds a reference (padding every stage's ref-strengths) when the empty lane is clicked", () => {
        const body = setup([{ duration: 5, frameRefs: [] }]);
        body.querySelector<HTMLElement>(
            '.vst-refs-lane[data-clip-idx="0"]',
        )?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const clips = lastSavedClips<Clip[]>(saveSpy);
        expect(clips[0].frameRefs).toHaveLength(1);
        expect(clips[0].stages[0].frameRefStrengths).toHaveLength(1);
        // The new ref opens in the dock immediately.
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 0 });
    });

    it("does not add a reference when the model publishes no positions", () => {
        const body = setup([{ duration: 5, frameRefs: [] }], undefined, []);
        body.querySelector<HTMLElement>(
            '.vst-refs-lane[data-clip-idx="0"]',
        )?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).not.toHaveBeenCalled();
        expect(getSelection()).toEqual({ kind: "none" });
    });

    it("adding a ref selects the NEW ref even while another ref is selected", () => {
        const body = setup([
            { duration: 5, frameRefs: [{ source: "Refiner" }] },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        body.querySelector<HTMLElement>(
            '.vst-refs-lane[data-clip-idx="0"]',
        )?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(lastSavedClips<Clip[]>(saveSpy)[0].frameRefs).toHaveLength(2);
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 1 });
    });

    it("uses the stored 16fps timeline when adding a ref", () => {
        const body = setup([{ duration: 5, frameRefs: [] }], 16);
        stubLaneRect(body, 0, 0, 120);
        body.querySelector<HTMLElement>(
            '.vst-refs-lane[data-clip-idx="0"]',
        )?.dispatchEvent(
            new MouseEvent("click", { bubbles: true, clientX: 60 }),
        );

        // Inclusive frame geometry makes the stored 16fps clip 81 frames long.
        expect(lastSavedClips<Clip[]>(saveSpy)[0].frameRefs[0].frame).toBe(41);
    });

    it("retimes a ref by dragging its thumbnail, suppressing the trailing click", () => {
        const body = setup([{ duration: 5, frameRefs: [{ frame: 1 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);

        dragThumb(thumb, 0, 60);
        // A completed drag swallows the trailing click → no selection, no add.
        thumb.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(getSelection().kind).toBe("none");
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(lastSavedClips<Clip[]>(saveSpy)[0].frameRefs[0].frame).toBe(61);
    });

    it("cancels a drag when a catalog refresh removes reference support", () => {
        const body = setup([{ duration: 5, frameRefs: [{ frame: 1 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);
        const originalLeft = thumb.style.left;

        thumb.dispatchEvent(
            new MouseEvent("mousedown", {
                bubbles: true,
                button: 0,
                clientX: 0,
            }),
        );
        document.dispatchEvent(
            new MouseEvent("mousemove", { bubbles: true, clientX: 60 }),
        );
        authoringCatalog.architectures[0].capabilities.features =
            authoringCatalog.architectures[0].capabilities.features.filter(
                (feature) => feature !== "frameReferences",
            );
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: 60 }),
        );

        expect(saveSpy).not.toHaveBeenCalled();
        expect(thumb.style.left).toBe(originalLeft);
        expect(thumb.querySelector(".vst-refs-ph")?.textContent).toBe("R 1");
    });

    it("cancels a drag when a catalog refresh changes reference endpoints", () => {
        const body = setup([{ duration: 5, frameRefs: [{ frame: 1 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);

        thumb.dispatchEvent(
            new MouseEvent("mousedown", {
                bubbles: true,
                button: 0,
                clientX: 0,
            }),
        );
        document.dispatchEvent(
            new MouseEvent("mousemove", { bubbles: true, clientX: 60 }),
        );
        for (const entry of authoringCatalog.entries) {
            entry.enhancements = {
                referencePositions: ["first", "last"],
            };
        }
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: 60 }),
        );

        expect(saveSpy).not.toHaveBeenCalled();
        expect(thumb.querySelector(".vst-refs-ph")?.textContent).toBe("R 1");
    });

    it("cancels a drag when a catalog repaint detaches its rendered lane", () => {
        const body = setup([{ duration: 5, frameRefs: [{ frame: 1 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);

        thumb.dispatchEvent(
            new MouseEvent("mousedown", {
                bubbles: true,
                button: 0,
                clientX: 0,
            }),
        );
        document.dispatchEvent(
            new MouseEvent("mousemove", { bubbles: true, clientX: 60 }),
        );
        body.innerHTML = "";
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: 60 }),
        );

        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("snaps a dragged reference to the nearest clip edge", () => {
        const body = setup([{ duration: 5, frameRefs: [{ frame: 1 }] }]);
        stubLaneRect(body, 0, 0, 120);
        dragThumb(markEl(body, 0, 0), 0, 116);

        expect(lastSavedClips<Clip[]>(saveSpy)[0].frameRefs[0].frame).toBe(121);
    });

    it("uses the stored 16fps timeline when dragging a ref", () => {
        const body = setup([{ duration: 5, frameRefs: [{ frame: 1 }] }], 16);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);

        dragThumb(thumb, 0, 60);

        expect(lastSavedClips<Clip[]>(saveSpy)[0].frameRefs[0].frame).toBe(41);
    });

    it("drags to the padded tail without aligning the effective frame count twice", async () => {
        const catalog = testArchitectureCatalog();
        const model = catalog.entries[0];
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(catalog),
        });
        await loadAuthoritativeArchitectureCatalog();
        mountSelect("input_videomodel", {
            options: [model.value],
            value: model.value,
        });
        const body = setup([{ duration: 6.5, frameRefs: [{ frame: 1 }] }], 4);
        stubLaneRect(body, 0, 0, 120);

        dragThumb(markEl(body, 0, 0), 0, 120);

        expect(lastSavedClips<Clip[]>(saveSpy)[0].frameRefs[0].frame).toBe(33);
    });

    it("drags a bounded reference between its advertised endpoints", async () => {
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
        const body = setup(
            [
                {
                    duration: 5,
                    frameRefs: [{ frame: 1, fromEnd: false }],
                },
            ],
            undefined,
            ["first", "last"],
        );
        stubLaneRect(body, 0, 0, 120);

        dragThumb(markEl(body, 0, 0), 0, 100);
        expect(lastSavedClips<Clip[]>(saveSpy)[0].frameRefs[0]).toMatchObject({
            frame: 1,
            fromEnd: true,
        });
        expect(
            markEl(body, 0, 0).querySelector(".vst-refs-ph")?.textContent,
        ).toBe("R -1");
    });

    it("treats a sub-threshold press as a select, not a drag", () => {
        const body = setup([{ duration: 5, frameRefs: [{ frame: 2 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);

        dragThumb(thumb, 40, 42); // 2px < the 5px drag threshold
        thumb.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).not.toHaveBeenCalled();
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 0 });
    });

    it("cancels an in-flight drag on Escape without saving", () => {
        const body = setup([{ duration: 5, frameRefs: [{ frame: 10 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);

        thumb.dispatchEvent(
            new MouseEvent("mousedown", {
                bubbles: true,
                button: 0,
                clientX: 0,
            }),
        );
        document.dispatchEvent(
            new MouseEvent("mousemove", { bubbles: true, clientX: 60 }),
        );
        document.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: 60 }),
        );
        thumb.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).not.toHaveBeenCalled();
        expect(getSelection().kind).toBe("none");
    });

    it("live-updates the label and holds position on a committed drag (no flash-back)", () => {
        const body = setup([{ duration: 5, frameRefs: [{ frame: 1 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);
        const originalLeft = thumb.style.left;

        dragThumb(thumb, 0, 60);

        expect(thumb.querySelector(".vst-refs-ph")?.textContent).toBe("R 61");
        expect(thumb.style.left).not.toBe(originalLeft);
        expect(lastSavedClips<Clip[]>(saveSpy)[0].frameRefs[0].frame).toBe(61);
    });

    it("drags the on-clip arrow together with the thumbnail", () => {
        mountPrompt([{ duration: 5, frameRefs: [{ frame: 1 }] }]);
        const body = mountTimelineBody();
        renderTimeline(body, persistence.getClips(), {
            pxPerSecond: TIMELINE_PPS,
        });
        const catalog = testArchitectureCatalog();
        for (const entry of catalog.entries) {
            entry.enhancements = { referencePositions: ["any"] };
        }
        track = createTimelineReferencesTrack(() =>
            testAuthoringTransactionSnapshot(catalog),
        );
        router = createGestureRouter();
        track.attach(body, router);
        router.attach(body);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);
        const arrow = body.querySelector<HTMLElement>(
            '.vst-key[data-ref-idx="0"]',
        );
        const originalLeft = thumb.style.left;

        thumb.dispatchEvent(
            new MouseEvent("mousedown", {
                bubbles: true,
                button: 0,
                clientX: 0,
            }),
        );
        document.dispatchEvent(
            new MouseEvent("mousemove", { bubbles: true, clientX: 60 }),
        );

        expect(thumb.style.left).not.toBe(originalLeft);
        expect(arrow?.style.left).toBe(thumb.style.left);
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: 60 }),
        );
    });
});
