import { describe, expect, it, jest } from "@jest/globals";
import {
    committedClips,
    crumbText,
    detail,
    detailBody,
    detailStripHarness,
    RETAKE_SOURCE,
    retakeFieldByLabel,
    sliderNumberByLabel,
} from "../__test_helpers__/detailStrip";
import { lastSavedClips } from "../__test_helpers__/dom";
import { getSelection, setSelection } from "../selection";
import type { Clip } from "../types";

describe("detail strip retake panel", () => {
    const h = detailStripHarness();
    const { setup } = h;

    it("shows a + Retake button on a clip without a retake and creates+selects one", () => {
        setup([{ duration: 4, stages: [{}], initVideo: RETAKE_SOURCE }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const addBtn = document.querySelector<HTMLElement>(
            ".vst-detail-add-retake",
        );
        expect(addBtn).not.toBeNull();
        addBtn?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
        const retake = committedClips()[0].retake;
        expect(retake).not.toBeNull();
        expect(retake?.startSeconds).toBe(0);
        expect(retake?.lengthSeconds).toBe(3); // min(default 3, clip 4)
        expect(retake?.strength).toBe(1);
    });

    it("shows no second Retake add action once a retake exists", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
                initVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(
            document.querySelector<HTMLButtonElement>(".vst-detail-add-retake"),
        ).toBeNull();
    });

    it("renders the retake editor with the breadcrumb, fields, note and remove", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 0.6 },
                initVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        expect(crumbText()).toBe("Retake · Clip 0 · 2–5 s");
        expect(
            retakeFieldByLabel("Start (s)").querySelector<HTMLInputElement>(
                "input",
            )?.value,
        ).toBe("2");
        expect(
            retakeFieldByLabel("Length (s)").querySelector<HTMLInputElement>(
                "input",
            )?.value,
        ).toBe("3");
        expect(sliderNumberByLabel("Strength").value).toBe("0.6");
        expect(
            detail()?.querySelector(".vst-detail-retake-col .vst-detail-note")
                ?.textContent,
        ).toContain("Applies when refining a base video");
        expect(
            detailBody()?.querySelector(".vst-detail-delete-retake")
                ?.textContent,
        ).toBe("×");
    });

    it("live-applies a retake Start edit through the debounce", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
                initVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        jest.useFakeTimers();
        const start =
            retakeFieldByLabel("Start (s)").querySelector<HTMLInputElement>(
                "input",
            );
        if (!start) {
            throw new Error("start input missing");
        }
        start.value = "4";
        start.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].retake?.startSeconds).toBe(
            4,
        );
    });

    it("a retake selection opens the CLIP panel with its Retake section", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
                initVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        // The clip panel (bare fields + Stages group) is what renders…
        expect(detailBody()?.querySelector(".vst-detail-clip")).not.toBeNull();
        expect(
            detail()?.querySelector('[data-vst-repeater-key="stages"]'),
        ).not.toBeNull();
        // …and its Retake section carries the editable fields.
        expect(
            detail()?.querySelector('[data-vst-accordion-key="retake"]'),
        ).not.toBeNull();
        expect(
            detail()?.querySelector('[data-vst-repeater-key="retakes"]'),
        ).toBeNull();
        expect(
            detail()
                ?.querySelector(".vst-detail-retake-section")
                ?.querySelector(".vst-detail-repeating-group"),
        ).toBeNull();
        expect(
            detailBody()?.querySelector(
                'input[data-vst-focus-key="retake-start"]',
            ),
        ).not.toBeNull();
        expect(crumbText()).toBe("Retake · Clip 0 · 2–5 s");
    });

    it("removes the retake without leaving or collapsing its section", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
                initVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        const beforeDelete = detailBody();
        if (!beforeDelete) {
            throw new Error("dock body missing");
        }
        beforeDelete.scrollTop = 140;
        beforeDelete
            ?.querySelector<HTMLElement>(".vst-detail-delete-retake")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(committedClips()[0].retake).toBeNull();
        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
        expect(
            detail()
                ?.querySelector('[data-vst-accordion-key="retake"]')
                ?.classList.contains("input-group-open"),
        ).toBe(true);
        expect(
            detail()?.querySelector(".vst-detail-add-retake"),
        ).not.toBeNull();
        expect(detailBody()?.scrollTop).toBe(140);
    });

    it("keeps the empty single-instance Retake section selectable", () => {
        setup([{ duration: 4, stages: [{}], initVideo: RETAKE_SOURCE }]);
        setSelection({ kind: "retake", clipIdx: 0 });
        expect(crumbText()).toBe("Retake · Clip 0");
        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
        expect(
            detail()
                ?.querySelector('[data-vst-accordion-key="retake"]')
                ?.classList.contains("input-group-open"),
        ).toBe(true);
    });
});
