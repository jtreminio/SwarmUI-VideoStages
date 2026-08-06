import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { captureAuthoringTransactionSnapshot } from "./authoringSnapshot";
import { createDetailSelectionOperations } from "./detailStrip/selectionOperations";
import { createGestureRouter, type GestureRouter } from "./gestureRouter";
import {
    getSelection,
    resetSelectionForTests,
    setSelection,
    subscribeSelection,
} from "./selection";
import { createTimelineLinking, type TimelineLinking } from "./timelineLinking";
import { applySelectionHighlight } from "./timelineSelectionView";

const SELECTED = "vst-region-selected";

const makeBody = (): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    body.innerHTML = [0, 1]
        .map(
            (idx) =>
                `<div class="vst-region" data-clip-idx="${idx}">` +
                `<button type="button" data-vst-stage data-clip-idx="${idx}" data-stage-idx="0">S0</button>` +
                `</div>`,
        )
        .join("");
    body.insertAdjacentHTML(
        "beforeend",
        '<button class="vst-boundary-chip" data-left-clip-idx="0">join</button>',
    );
    document.body.appendChild(body);
    return body;
};

const region = (body: HTMLElement, idx: number): HTMLElement => {
    const el = body.querySelector<HTMLElement>(
        `.vst-region[data-clip-idx="${idx}"]`,
    );
    if (!el) {
        throw new Error(`region ${idx} not found`);
    }
    return el;
};

const selectedRegions = (body: HTMLElement): string[] =>
    Array.from(body.querySelectorAll(`.${SELECTED}`)).map(
        (el) => el.getAttribute("data-clip-idx") ?? "?",
    );

describe("applySelectionHighlight owns the clip highlight", () => {
    let linking: TimelineLinking | null = null;
    let router: GestureRouter | null = null;

    beforeEach(() => {
        resetSelectionForTests();
    });

    afterEach(() => {
        linking?.dispose();
        router?.dispose();
        linking = null;
        router = null;
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    it("moves the highlight when a stage chip selects another clip", () => {
        const body = makeBody();
        linking = createTimelineLinking();
        router = createGestureRouter();
        router.attach(body);
        linking.attach(body, router);
        // The live wiring: the sole highlight owner runs off the selection.
        subscribeSelection(() => applySelectionHighlight(body));
        // The detail strip claims stage-chip clicks in the capture phase, so
        // the linking click handler never sees them.
        const operations = createDetailSelectionOperations(
            jest.fn(),
            captureAuthoringTransactionSnapshot,
        );
        body.addEventListener(
            "click",
            (event) => operations.onClickCapture(event as MouseEvent),
            true,
        );

        region(body, 0).dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );
        expect(selectedRegions(body)).toEqual(["0"]);

        region(body, 1)
            .querySelector("[data-vst-stage]")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 1,
            stageIdx: 0,
        });
        expect(selectedRegions(body)).toEqual(["1"]);
    });

    it("clears the clip highlight for non-clip selections", () => {
        const body = makeBody();
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        applySelectionHighlight(body);
        expect(selectedRegions(body)).toEqual(["1"]);

        setSelection({ kind: "audio-track", trackIdx: 0 });
        applySelectionHighlight(body);
        expect(selectedRegions(body)).toEqual([]);
    });

    it("highlights the source boundary for an automatic reference", () => {
        const body = makeBody();
        setSelection({ kind: "boundary-ref", leftClipIdx: 0 });

        applySelectionHighlight(body);

        expect(
            body
                .querySelector(".vst-boundary-chip")
                ?.classList.contains("vst-selected"),
        ).toBe(true);
    });
});
