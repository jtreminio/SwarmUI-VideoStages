import { describe, expect, it, jest } from "@jest/globals";
import { lastSavedClips } from "../__test_helpers__/dom";
import {
    committedClips,
    editorHarness,
    RETAKE_SOURCE,
} from "../__test_helpers__/editor";
import { getSelection, setSelection } from "../selection";
import type { Clip } from "../types";

describe("production editor retake panel", () => {
    const h = editorHarness();
    const detail = (): HTMLElement => h.section("retake");
    const sliderNumberByLabel = (label: string): HTMLInputElement =>
        h.field("retake", label, "input.auto-slider-number");

    it("shows a + Retake button on a clip without a retake and creates+selects one", () => {
        h.setup([{ duration: 4, stages: [{}], initVideo: RETAKE_SOURCE }]);
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
        expect(retake?.lengthSeconds).toBe(3);
        expect(retake?.strength).toBe(1);
    });

    it("keeps an existing retake editable", () => {
        h.setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
                initVideo: RETAKE_SOURCE,
            },
        ]);
        expect(h.field("retake", "Start (s)").value).toBe("2");
        expect(
            h.section("retake").querySelector("[data-testid=remove-item]")
                ?.textContent,
        ).toBe("Delete");
    });

    it("renders the retake section with its fields, note and delete action", () => {
        h.setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 0.6 },
                initVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        expect(h.section("retake").querySelector("h3")?.textContent).toBe(
            "Retake",
        );
        expect(h.field("retake", "Start (s)")?.value).toBe("2");
        expect(h.field("retake", "Length (s)")?.value).toBe("3");
        expect(sliderNumberByLabel("Strength").value).toBe("0.6");
        expect(
            detail()?.querySelector(".vst-detail-retake-col .vst-detail-note")
                ?.textContent,
        ).toContain("Applies when refining a base video");
        expect(
            h.section("retake").querySelector("[data-testid=remove-item]")
                ?.textContent,
        ).toBe("Delete");
    });

    it("live-applies a retake Start edit through the debounce", () => {
        h.setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
                initVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        jest.useFakeTimers();
        const start = h.field("retake", "Start (s)");
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

    it("navigates to editable retake fields from a retake selection", () => {
        h.setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
                initVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        expect(
            h.root
                .querySelector('[data-testid="nav-retake"]')
                ?.getAttribute("aria-current"),
        ).toBe("location");
        expect(h.field("retake", "Start (s)").value).toBe("2");
    });

    it("removes a retake and offers creating another", () => {
        h.setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
                initVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        h.section("retake")
            .querySelector<HTMLButtonElement>("[data-testid=remove-item]")
            ?.click();
        expect(committedClips()[0].retake).toBeNull();
        const add = h
            .section("retake")
            .querySelector<HTMLButtonElement>(".vst-detail-add-retake");
        expect(add?.disabled).toBe(false);
        add?.click();
        expect(committedClips()[0].retake?.startSeconds).toBe(0);
    });

    it("keeps the empty Retake section selectable", () => {
        h.setup([{ duration: 4, stages: [{}], initVideo: RETAKE_SOURCE }]);
        setSelection({ kind: "retake", clipIdx: 0 });
        expect(
            h.root
                .querySelector('[data-testid="nav-retake"]')
                ?.getAttribute("aria-current"),
        ).toBe("location");
        expect(
            h.section("retake").querySelector(".vst-detail-add-retake")
                ?.textContent,
        ).toBe("+ Add Retake");
    });
});
