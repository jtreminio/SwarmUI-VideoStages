import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
import * as persistence from "./persistence";
import {
    createTimelineAudioTrack,
    type TimelineAudioTrack,
} from "./timelineAudioTrack";
import { computeRegionLayout, renderAudioTrackRow } from "./timelineView";
import type { Clip } from "./types";

const PPS = 44;

interface ClipFixture {
    duration: number;
    audioSource?: string;
    reuseAudio?: boolean;
    controlNetLora?: string;
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    audioSource: clip.audioSource ?? "Native",
    reuseAudio: clip.reuseAudio ?? false,
    controlNetLora: clip.controlNetLora ?? "",
    stages: [{}],
    refs: [],
    promptWindows: [],
});

const mountPrompt = (clips: ClipFixture[]): HTMLTextAreaElement => {
    mountPromptBox("");
    return mountVideoStagesData({ clips: clips.map(clipRecord) });
};

const makeBody = (): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    body.dataset.vstPps = String(PPS);
    document.body.appendChild(body);
    return body;
};

const renderAudio = (body: HTMLElement, clips: Clip[]): void => {
    const layouts = computeRegionLayout(clips, { pxPerSecond: PPS });
    body.innerHTML = renderAudioTrackRow(clips, layouts);
};

const inspector = (): HTMLElement | null =>
    document.querySelector<HTMLElement>(".vst-audio-inspector");

const fieldByLabel = (label: string): HTMLElement => {
    const rows = Array.from(
        document.querySelectorAll<HTMLElement>(
            ".vst-audio-inspector .vst-audio-field",
        ),
    );
    const row = rows.find(
        (r) => r.querySelector(".vst-audio-field-label")?.textContent === label,
    );
    if (!row) {
        throw new Error(`audio field not found: ${label}`);
    }
    return row;
};

const checkboxByLabel = (label: string): HTMLInputElement => {
    const input = fieldByLabel(label).querySelector<HTMLInputElement>(
        "input[type='checkbox']",
    );
    if (!input) {
        throw new Error(`checkbox not found: ${label}`);
    }
    return input;
};

// The popover applies changes automatically when it closes; a genuine
// outside mousedown is the close gesture.
const clickAway = (body: HTMLElement): void => {
    body.dispatchEvent(new MouseEvent("mousedown", { bubbles: true }));
};

const savedClips = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
): Clip[] => spy.mock.calls[spy.mock.calls.length - 1][0] as Clip[];

describe("createTimelineAudioTrack (per-clip audio editor)", () => {
    let track: TimelineAudioTrack | null = null;
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
        renderAudio(body, persistence.getClips());
        track = createTimelineAudioTrack();
        track.attach(body);
        return body;
    };

    const openClip = (body: HTMLElement, clipIdx: number): void => {
        const seg = body.querySelector<HTMLElement>(
            `.vst-audio-clip[data-clip-idx="${clipIdx}"]`,
        );
        if (!seg) {
            throw new Error(`audio segment not found: clip ${clipIdx}`);
        }
        seg.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    };

    it("opens an inspector when a native audio segment is clicked", () => {
        const body = setup([{ duration: 5 }]);
        expect(inspector()).toBeNull();
        openClip(body, 0);
        expect(inspector()).not.toBeNull();
        const options = Array.from(
            document.querySelectorAll<HTMLOptionElement>(
                ".vst-audio-select option",
            ),
        ).map((o) => o.value);
        expect(options).toEqual(["Native", "Upload"]);
    });

    it("offers ControlNet as a source only when the clip has a ControlNet LoRA", () => {
        const body = setup([{ duration: 5, controlNetLora: "some-lora" }]);
        openClip(body, 0);
        const options = Array.from(
            document.querySelectorAll<HTMLOptionElement>(
                ".vst-audio-select option",
            ),
        ).map((o) => o.value);
        expect(options).toContain("ControlNet");
    });

    it("wraps the source <select> in a div, not a label", () => {
        // A <label> parent forwards option clicks (from SwarmUI's spawned
        // dropdown popover) back to the <select>, reopening it. The field
        // container must therefore not be a <label>.
        const body = setup([{ duration: 5 }]);
        openClip(body, 0);
        const select =
            document.querySelector<HTMLSelectElement>(".vst-audio-select");
        const field = select?.closest(".vst-audio-field");
        expect(field?.tagName).toBe("DIV");
    });

    it("applies a source change automatically on click-away", () => {
        const body = setup([{ duration: 5 }]);
        openClip(body, 0);
        const select =
            document.querySelector<HTMLSelectElement>(".vst-audio-select");
        if (!select) {
            throw new Error("source select not found");
        }
        select.value = "Upload";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        clickAway(body);

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].audioSource).toBe("Upload");
        expect(inspector()).toBeNull();
    });

    it("does not dismiss when interacting with the core dropdown popover", () => {
        // SwarmUI spawns a `.sui-popover` for the <select>; mousedowns within
        // it must not close the editor (or the source could never be picked).
        const body = setup([{ duration: 5 }]);
        openClip(body, 0);
        const pop = document.createElement("div");
        pop.className = "sui-popover";
        document.body.appendChild(pop);
        pop.dispatchEvent(new MouseEvent("mousedown", { bubbles: true }));

        expect(saveSpy).not.toHaveBeenCalled();
        expect(inspector()).not.toBeNull();
        pop.remove();
    });

    it("persists the Reuse Audio toggle on click-away", () => {
        const body = setup([{ duration: 5 }]);
        openClip(body, 0);
        const reuse = checkboxByLabel("Reuse Audio");
        reuse.checked = true;
        reuse.dispatchEvent(new Event("change", { bubbles: true }));
        clickAway(body);

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].reuseAudio).toBe(true);
    });

    it("discards edits when Escape is pressed", () => {
        const body = setup([{ duration: 5 }]);
        openClip(body, 0);
        const select =
            document.querySelector<HTMLSelectElement>(".vst-audio-select");
        if (select) {
            select.value = "Upload";
            select.dispatchEvent(new Event("change", { bubbles: true }));
        }
        inspector()?.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );

        expect(saveSpy).not.toHaveBeenCalled();
        expect(inspector()).toBeNull();
    });

    it("disables Clip Length from Audio for Native but enables it for Upload", () => {
        const body = setup([{ duration: 5 }]);
        openClip(body, 0);
        expect(checkboxByLabel("Clip Length from Audio").disabled).toBe(true);
        const select =
            document.querySelector<HTMLSelectElement>(".vst-audio-select");
        if (!select) {
            throw new Error("source select not found");
        }
        select.value = "Upload";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(checkboxByLabel("Clip Length from Audio").disabled).toBe(false);
    });
});
