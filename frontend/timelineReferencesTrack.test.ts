import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import * as persistence from "./persistence";
import {
    createTimelineReferencesTrack,
    type TimelineReferencesTrack,
} from "./timelineReferencesTrack";
import {
    computeRegionLayout,
    renderReferencesTrackRow,
    renderTimeline,
} from "./timelineView";
import type { Clip } from "./types";

const PPS = 44;

interface RefFixture {
    source?: string;
    frame?: number;
    fromEnd?: boolean;
    uploadFileName?: string | null;
    uploadedImage?: { data: string; fileName: string | null } | null;
}

interface ClipFixture {
    duration: number;
    refs?: RefFixture[];
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    audioSource: "Native",
    stages: [{}],
    refs: (clip.refs ?? []).map((ref) => ({
        source: ref.source ?? "Refiner",
        frame: ref.frame ?? 1,
        fromEnd: ref.fromEnd ?? false,
        uploadFileName: ref.uploadFileName ?? null,
        uploadedImage: ref.uploadedImage ?? null,
    })),
    promptWindows: [],
});

const mountPrompt = (clips: ClipFixture[]): HTMLTextAreaElement => {
    const json = JSON.stringify({ clips: clips.map(clipRecord) });
    const input = document.createElement("textarea");
    input.id = "input_prompt";
    input.value = `<videostages>${json}`;
    document.body.appendChild(input);
    return input;
};

const makeBody = (): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    body.dataset.vstPps = String(PPS);
    document.body.appendChild(body);
    return body;
};

const renderRefs = (body: HTMLElement, clips: Clip[]): void => {
    const layouts = computeRegionLayout(clips, { pxPerSecond: PPS });
    body.innerHTML = renderReferencesTrackRow(clips, layouts, 24, "seconds");
};

const inspector = (): HTMLElement | null =>
    document.querySelector<HTMLElement>(".vst-refs-inspector");

const sourceSelect = (): HTMLSelectElement => {
    const select = document.querySelector<HTMLSelectElement>(
        ".vst-refs-inspector select.vst-audio-select",
    );
    if (!select) {
        throw new Error("source select not found");
    }
    return select;
};

const frameInput = (): HTMLInputElement => {
    const input = document.querySelector<HTMLInputElement>(
        ".vst-refs-inspector .vst-refs-num",
    );
    if (!input) {
        throw new Error("frame input not found");
    }
    return input;
};

const clickAway = (body: HTMLElement): void => {
    body.dispatchEvent(new MouseEvent("mousedown", { bubbles: true }));
};

const savedClips = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
): Clip[] => spy.mock.calls[spy.mock.calls.length - 1][0] as Clip[];

describe("createTimelineReferencesTrack (per-ref image editor)", () => {
    let track: TimelineReferencesTrack | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;

    beforeEach(() => {
        saveSpy = jest
            .spyOn(persistence, "saveClips")
            .mockImplementation(() => {});
    });

    afterEach(() => {
        track?.dispose();
        track = null;
        jest.restoreAllMocks();
        document.body.innerHTML = "";
    });

    const setup = (fixtures: ClipFixture[]): HTMLElement => {
        mountPrompt(fixtures);
        const body = makeBody();
        renderRefs(body, persistence.getClips());
        track = createTimelineReferencesTrack();
        track.attach(body);
        return body;
    };

    const openThumb = (
        body: HTMLElement,
        clipIdx: number,
        refIdx: number,
    ): void => {
        const thumb = body.querySelector<HTMLElement>(
            `.vst-refs-mark[data-clip-idx="${clipIdx}"][data-ref-idx="${refIdx}"]`,
        );
        if (!thumb) {
            throw new Error(
                `ref thumb not found: clip ${clipIdx} ref ${refIdx}`,
            );
        }
        thumb.dispatchEvent(new MouseEvent("click", { bubbles: true }));
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
        lane.getBoundingClientRect = (() =>
            ({
                left,
                width,
                right: left + width,
                top: 0,
                bottom: 40,
                height: 40,
                x: left,
                y: 0,
                toJSON: () => ({}),
            }) as DOMRect) as HTMLElement["getBoundingClientRect"];
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

    it("opens an inspector with the ref source options when a thumb is clicked", () => {
        const body = setup([{ duration: 5, refs: [{ source: "Refiner" }] }]);
        expect(inspector()).toBeNull();
        openThumb(body, 0, 0);
        expect(inspector()).not.toBeNull();
        const options = Array.from(sourceSelect().options).map((o) => o.value);
        expect(options).toEqual(["Base", "Refiner", "Upload"]);
    });

    it("wraps the source <select> in a div, not a label", () => {
        // A <label> parent forwards SwarmUI dropdown-popover option clicks back
        // to the <select>, reopening it — the field container must be a <div>.
        const body = setup([{ duration: 5, refs: [{ source: "Refiner" }] }]);
        openThumb(body, 0, 0);
        const field = sourceSelect().closest(".vst-audio-field");
        expect(field?.tagName).toBe("DIV");
    });

    it("does not dismiss when interacting with the core dropdown popover", () => {
        const body = setup([{ duration: 5, refs: [{ source: "Refiner" }] }]);
        openThumb(body, 0, 0);
        const pop = document.createElement("div");
        pop.className = "sui-popover";
        document.body.appendChild(pop);
        pop.dispatchEvent(new MouseEvent("mousedown", { bubbles: true }));

        expect(saveSpy).not.toHaveBeenCalled();
        expect(inspector()).not.toBeNull();
        pop.remove();
    });

    it("applies a source change to Upload automatically on click-away", () => {
        const body = setup([{ duration: 5, refs: [{ source: "Refiner" }] }]);
        openThumb(body, 0, 0);
        const select = sourceSelect();
        select.value = "Upload";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        clickAway(body);

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].refs[0].source).toBe("Upload");
        expect(inspector()).toBeNull();
    });

    it("commits an edited frame, clamped to the clip's frame count", () => {
        const body = setup([{ duration: 5, refs: [{ frame: 1 }] }]);
        openThumb(body, 0, 0);
        const input = frameInput();
        input.value = "7";
        input.dispatchEvent(new Event("change", { bubbles: true }));
        clickAway(body);

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].refs[0].frame).toBe(7);
    });

    it("clamps an out-of-range frame to the clip's legal range on commit", () => {
        const body = setup([{ duration: 5, refs: [{ frame: 2 }] }]);
        openThumb(body, 0, 0);
        const input = frameInput();
        // Below the minimum snaps to REF_FRAME_MIN (1); the backend has no
        // upper clamp, so an over-max forward frame would crash generation.
        input.value = "-5";
        input.dispatchEvent(new Event("change", { bubbles: true }));
        clickAway(body);
        expect(savedClips(saveSpy)[0].refs[0].frame).toBe(1);

        const body2 = body;
        openThumb(body2, 0, 0);
        const input2 = frameInput();
        input2.value = "999999";
        input2.dispatchEvent(new Event("change", { bubbles: true }));
        clickAway(body2);
        const frame = savedClips(saveSpy)[0].refs[0].frame;
        expect(frame).toBeGreaterThanOrEqual(1);
        expect(frame).toBeLessThan(999999);
    });

    it("clears the uploaded image and filename when the source leaves Upload", () => {
        const body = setup([
            {
                duration: 5,
                refs: [
                    {
                        source: "Upload",
                        uploadFileName: "cat.png",
                        uploadedImage: {
                            data: "data:image/png;base64,QQ==",
                            fileName: "cat.png",
                        },
                    },
                ],
            },
        ]);
        openThumb(body, 0, 0);
        const select = sourceSelect();
        select.value = "Refiner";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        clickAway(body);

        const ref = savedClips(saveSpy)[0].refs[0];
        expect(ref.source).toBe("Refiner");
        expect(ref.uploadedImage).toBeNull();
        expect(ref.uploadFileName).toBeNull();
    });

    it("clears the top-level uploadFileName (not just the image) via the Clear button", () => {
        const body = setup([
            {
                duration: 5,
                refs: [
                    {
                        source: "Upload",
                        uploadFileName: "cat.png",
                        uploadedImage: {
                            data: "data:image/png;base64,QQ==",
                            fileName: "cat.png",
                        },
                    },
                ],
            },
        ]);
        openThumb(body, 0, 0);
        const clear = document.querySelector<HTMLButtonElement>(
            ".vst-refs-inspector .vst-audio-upload-clear",
        );
        if (!clear) {
            throw new Error("clear button not found");
        }
        clear.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        // Stay on Upload; click away to commit.
        clickAway(body);

        const ref = savedClips(saveSpy)[0].refs[0];
        expect(ref.uploadedImage).toBeNull();
        expect(ref.uploadFileName).toBeNull();
    });

    it("toggles from-end on click-away", () => {
        const body = setup([{ duration: 5, refs: [{ fromEnd: false }] }]);
        openThumb(body, 0, 0);
        const check = document.querySelector<HTMLInputElement>(
            ".vst-refs-inspector input[type='checkbox']",
        );
        if (!check) {
            throw new Error("from-end checkbox not found");
        }
        check.checked = true;
        check.dispatchEvent(new Event("change", { bubbles: true }));
        clickAway(body);

        expect(savedClips(saveSpy)[0].refs[0].fromEnd).toBe(true);
    });

    it("discards edits when Escape is pressed", () => {
        const body = setup([{ duration: 5, refs: [{ source: "Refiner" }] }]);
        openThumb(body, 0, 0);
        const select = sourceSelect();
        select.value = "Upload";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        inspector()?.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );

        expect(saveSpy).not.toHaveBeenCalled();
        expect(inspector()).toBeNull();
    });

    it("deletes the reference (and its stage ref-strengths) via shift+click", () => {
        const body = setup([
            { duration: 5, refs: [{ source: "Refiner" }, { source: "Base" }] },
        ]);
        markEl(body, 0, 0).dispatchEvent(
            new MouseEvent("click", { bubbles: true, shiftKey: true }),
        );

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const clips = savedClips(saveSpy);
        expect(clips[0].refs).toHaveLength(1);
        expect(clips[0].refs[0].source).toBe("Base");
        // refStrengths stay index-aligned with refs.
        expect(clips[0].stages[0].refStrengths).toHaveLength(1);
        expect(inspector()).toBeNull();
    });

    it("deletes the reference (and its stage ref-strengths) via the popover", () => {
        const body = setup([
            { duration: 5, refs: [{ source: "Refiner" }, { source: "Base" }] },
        ]);
        openThumb(body, 0, 0);
        const del = document.querySelector<HTMLButtonElement>(
            ".vst-refs-inspector .vst-refs-delete",
        );
        if (!del) {
            throw new Error("delete button not found");
        }
        del.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const clips = savedClips(saveSpy);
        expect(clips[0].refs).toHaveLength(1);
        expect(clips[0].refs[0].source).toBe("Base");
        // refStrengths stay index-aligned with refs.
        expect(clips[0].stages[0].refStrengths).toHaveLength(1);
        expect(inspector()).toBeNull();
    });

    it("adds a reference (padding every stage's ref-strengths) when the empty lane is clicked", () => {
        const body = setup([{ duration: 5, refs: [] }]);
        const lane = body.querySelector<HTMLElement>(
            '.vst-refs-lane[data-clip-idx="0"]',
        );
        if (!lane) {
            throw new Error("ref lane not found");
        }
        lane.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const clips = savedClips(saveSpy);
        expect(clips[0].refs).toHaveLength(1);
        expect(clips[0].stages[0].refStrengths).toHaveLength(1);
    });

    it("retimes a ref by dragging its thumbnail, suppressing the trailing click", () => {
        const body = setup([{ duration: 5, refs: [{ frame: 1 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);

        dragThumb(thumb, 0, 60);
        // A completed drag swallows the click, so no editor opens and the lane
        // does not add a second ref.
        thumb.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(inspector()).toBeNull();
        expect(saveSpy).toHaveBeenCalledTimes(1);
        // Half-way across a 5s clip @24fps → frame 60.
        expect(savedClips(saveSpy)[0].refs[0].frame).toBe(60);
    });

    it("treats a sub-threshold press as an editor click, not a drag", () => {
        const body = setup([{ duration: 5, refs: [{ frame: 2 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);

        dragThumb(thumb, 40, 42); // 2px < the 5px drag threshold
        thumb.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).not.toHaveBeenCalled();
        expect(inspector()).not.toBeNull();
    });

    it("cancels an in-flight drag on Escape without saving", () => {
        const body = setup([{ duration: 5, refs: [{ frame: 10 }] }]);
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
        expect(inspector()).toBeNull();
    });

    it("live-updates the label and holds position on a committed drag (no flash-back)", () => {
        const body = setup([{ duration: 5, refs: [{ frame: 1 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);
        const originalLeft = thumb.style.left;

        dragThumb(thumb, 0, 60);

        // Label tracks the dropped frame, and the marker is NOT snapped back to
        // its original spot (that would flash before the async re-render).
        expect(thumb.querySelector(".vst-refs-ph")?.textContent).toBe("R 60");
        expect(thumb.style.left).toBe("50%");
        expect(thumb.style.left).not.toBe(originalLeft);
        expect(savedClips(saveSpy)[0].refs[0].frame).toBe(60);
    });

    it("drags the on-clip arrow together with the thumbnail", () => {
        mountPrompt([{ duration: 5, refs: [{ frame: 1 }] }]);
        const body = makeBody();
        renderTimeline(body, persistence.getClips(), { pxPerSecond: PPS });
        track = createTimelineReferencesTrack();
        track.attach(body);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);
        const arrow = body.querySelector<HTMLElement>(
            '.vst-key[data-ref-idx="0"]',
        );

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

        // The arrow shares the thumbnail's percent so both stay x-aligned.
        expect(arrow?.style.left).toBe("50%");
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: 60 }),
        );
    });

    it("previews frame edits from the popover, reverting them on Escape", () => {
        const body = setup([{ duration: 5, refs: [{ frame: 1 }] }]);
        const thumb = markEl(body, 0, 0);
        const originalLeft = thumb.style.left;
        openThumb(body, 0, 0);

        const input = frameInput();
        input.value = "60";
        input.dispatchEvent(new Event("input", { bubbles: true }));
        // The thumbnail slides live as the frame number changes.
        expect(thumb.style.left).toBe("50%");
        expect(thumb.querySelector(".vst-refs-ph")?.textContent).toBe("R 60");

        inspector()?.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
        // Escape discards the edit and restores the rendered position/label.
        expect(saveSpy).not.toHaveBeenCalled();
        expect(thumb.style.left).toBe(originalLeft);
        expect(thumb.querySelector(".vst-refs-ph")?.textContent).toBe("R 1");
    });
});
