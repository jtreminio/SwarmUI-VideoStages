import { describe, expect, it, jest } from "@jest/globals";
import { initVideoFixture } from "./__test_helpers__/clipFixtures";
import {
    committedClips,
    crumbText,
    detail,
    detailBody,
    detailStripHarness,
    dockHost,
    fieldByLabel,
    minorEditor,
    minorRows,
    RETAKE_SOURCE,
    refRow,
    sliderNumberByLabel,
} from "./__test_helpers__/detailStrip";
import { lastSavedClips } from "./__test_helpers__/dom";
import * as persistence from "./persistence/repository";
import { activateSelection, getSelection, setSelection } from "./selection";
import { renderTimeline } from "./timelineView";
import type { Clip } from "./types";

describe("createTimelineDetailStrip", () => {
    const h = detailStripHarness();

    const railChips = (): HTMLElement[] =>
        Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .vst-stage-tab",
            ),
        );

    const activeRailLabel = (): string | undefined =>
        document
            .querySelector<HTMLElement>(
                '.vst-detail .vst-stage-tab[aria-pressed="true"] .header-label',
            )
            ?.textContent?.replace(/^Stage /, "") ?? undefined;

    const clickRegionStageChip = (
        body: HTMLElement,
        clipIdx: number,
        stageIdx: number,
        shift = false,
    ): void => {
        const chip = body.querySelector<HTMLElement>(
            `[data-vst-stage][data-clip-idx="${clipIdx}"][data-stage-idx="${stageIdx}"]`,
        );
        if (!chip) {
            throw new Error(`stage chip not found: ${clipIdx}/${stageIdx}`);
        }
        chip.dispatchEvent(
            new MouseEvent("click", { bubbles: true, shiftKey: shift }),
        );
    };

    it("renders the timeline settings panel when nothing is selected", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        expect(detail()).not.toBeNull();
        expect(crumbText()).toBe("Timeline settings");
        expect(detail()?.querySelector(".vst-detail-settings")).not.toBeNull();
        const labels = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .vst-detail-field-label",
            ),
        ).map((el) => el.textContent);
        expect(labels).toEqual(expect.arrayContaining(["Aspect Ratio", "FPS"]));
        // Width/Height sliders only exist while Aspect Ratio is Custom.
        expect(labels).not.toContain("Width");
        expect(labels).not.toContain("Height");
        expect(
            detail()?.querySelector(
                'input[data-vst-focus-key="settings-side-length"]',
            ),
        ).toBeNull();
        expect(
            detail()?.querySelector(".vst-settings-calculated-dims"),
        ).toBeNull();
        // The FPS field is always editable — it mirrors the core Video FPS.
        expect(
            detail()?.querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="settings-fps"]',
            )?.disabled,
        ).toBe(false);
        expect(detail()?.querySelector(".vst-audio-tracks-panel")).toBeNull();
    });

    it("honors inert rendering when reattached to the same body and dock", () => {
        const body = h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const dock = dockHost(body);
        const rendered = dock.querySelector(".vst-detail-body");
        expect(rendered).not.toBeNull();

        h.strip.attach(body, dock, false);
        setSelection({ kind: "prompt-major", clipIdx: 0 });

        expect(dock.querySelector(".vst-detail-body")).toBe(rendered);
    });

    it("renders the clip/stage columns when a stage chip is clicked", () => {
        const body = h.setup([{ duration: 4, stages: [{}, {}] }]);
        clickRegionStageChip(body, 0, 1);
        expect(crumbText()).toBe("Clip 0 · S1");
        expect(activeRailLabel()).toBe("S1");
        expect(detailBody()?.querySelector(".vst-detail-clip")).not.toBeNull();
        expect(
            detailBody()?.querySelector(".vst-detail-repeating-group"),
        ).not.toBeNull();
        expect(
            detailBody()?.querySelector(".vst-detail-params"),
        ).not.toBeNull();
        // LoRA model definitions are clip-level, not nested in stage fields.
        const loras = detailBody()?.querySelector<HTMLElement>(
            ".vst-detail-loras-section",
        );
        expect(loras).not.toBeNull();
    });

    it("opens every empty addable repeater so its Add action is visible", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                frameRefs: [],
                icLoras: [],
                windows: [],
            },
        ]);
        const expectOpenAdd = (
            sectionSelector: string,
            addSelector: string,
        ): void => {
            const section =
                document.querySelector<HTMLElement>(sectionSelector);
            expect(section).not.toBeNull();
            expect(section?.classList.contains("input-group-open")).toBe(true);
            expect(
                section?.querySelector<HTMLElement>(
                    ":scope > .vst-detail-section-content",
                )?.hidden,
            ).toBe(false);
            expect(section?.querySelector(addSelector)).not.toBeNull();
        };

        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expectOpenAdd(".vst-detail-ref-section", ".vst-detail-add-ref");
        expectOpenAdd(".vst-detail-loras-section", ".vst-detail-add-lora");
        expectOpenAdd(".vst-detail-iclora-section", ".vst-detail-add-iclora");

        setSelection({ kind: "prompt-major", clipIdx: 0 });
        expectOpenAdd(".vst-detail-relay-section", ".vst-detail-add-relay");

        setSelection({ kind: "audio", clipIdx: 0 });
        expectOpenAdd(".vst-audio-tracks-panel", ".vst-audio-track-add");
    });

    it("switches the active stage when a rail chip is clicked", () => {
        h.setup([{ duration: 4, stages: [{ steps: 5 }, { steps: 9 }] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(activeRailLabel()).toBe("S0");
        expect(sliderNumberByLabel("Steps").value).toBe("5");

        railChips()[1].dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );
        expect(activeRailLabel()).toBe("S1");
        expect(sliderNumberByLabel("Steps").value).toBe("9");
    });

    it("omits help popovers from the basic stage sampling fields", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        for (const text of [
            "Model",
            "Steps",
            "CFG Scale",
            "Sampler",
            "Scheduler",
        ]) {
            const label = Array.from(
                detailBody()?.querySelectorAll<HTMLElement>(
                    ".vst-detail-params .auto-input-name",
                ) ?? [],
            ).find((candidate) => candidate.textContent === text);
            expect(label).not.toBeUndefined();
            expect(
                label
                    ?.closest(".vst-detail-field, .vst-stage-slider")
                    ?.querySelector(".info-popover-button"),
            ).toBeNull();
        }
    });

    it("uses zero-based Keyframe labels and opens each newly added keyframe", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-add-ref")
            ?.click();
        document
            .querySelector<HTMLButtonElement>(".vst-detail-add-ref")
            ?.click();
        const groups = document.querySelectorAll<HTMLElement>(
            ".vst-detail-ref-section .vst-detail-repeating-group",
        );
        expect(groups).toHaveLength(2);
        expect(groups[0].querySelector(".header-label")?.textContent).toBe(
            "Keyframe 0",
        );
        expect(groups[1].querySelector(".header-label")?.textContent).toBe(
            "Keyframe 1",
        );
        expect(groups[0].classList.contains("input-group-closed")).toBe(true);
        expect(groups[1].classList.contains("input-group-open")).toBe(true);
        expect(sliderNumberByLabel("Keyframe 0")).not.toBeNull();
        expect(sliderNumberByLabel("Keyframe 1")).not.toBeNull();
    });

    it("places Count from clip end help before its label", () => {
        h.setup([
            {
                duration: 4,
                stages: [{}],
                frameRefs: [{ source: "Upload", frame: 0 }],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        const row = Array.from(
            detailBody()?.querySelectorAll<HTMLElement>(
                ".vst-detail-field-check",
            ) ?? [],
        ).find((candidate) =>
            candidate.textContent?.includes("Count from clip end"),
        );
        const label = row?.querySelector<HTMLElement>("label");
        expect(label?.firstElementChild?.textContent).toBe("?");
        expect(label?.lastElementChild?.textContent).toBe(
            "Count from clip end",
        );
    });

    it("places the init-video explanation at the top of its group", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const content = detailBody()?.querySelector<HTMLElement>(
            ".vst-detail-source-col",
        );
        expect(content?.firstElementChild?.textContent).toBe(
            "Use an existing video file as this clip instead of generating it.",
        );
        expect(
            content?.firstElementChild?.classList.contains(
                "vst-detail-field-hint",
            ),
        ).toBe(true);
    });

    it("shows Control/Upscale only on refine stages", () => {
        h.setup([{ duration: 4, stages: [{}, {}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(
            document.querySelector(".vst-detail .auto-input-name"),
        ).not.toBeNull();
        const labels0 = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .auto-input-name",
            ),
        ).map((el) => el.textContent);
        expect(labels0).not.toContain("Control");
        expect(labels0).not.toContain("Upscale");

        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        const labels1 = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .auto-input-name",
            ),
        ).map((el) => el.textContent);
        expect(labels1).toContain("Control");
        expect(labels1).toContain("Upscale");
        expect(
            fieldByLabel("Upscale Method").querySelector("select"),
        ).not.toBeNull();
    });

    it("clears the selection to none on Escape", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(crumbText()).toBe("Clip 0 · S0");
        detail()?.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
        expect(crumbText()).toBe("Timeline settings");
        expect(getSelection().kind).toBe("none");
    });

    it("keeps the selection when Escape fires inside a .sui-popover", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const popover = document.createElement("div");
        popover.className = "sui-popover";
        const search = document.createElement("input");
        popover.appendChild(search);
        detail()?.appendChild(popover);
        search.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
        expect(crumbText()).toBe("Clip 0 · S0");
    });

    it("shift+clicking a region stage chip deletes the stage", () => {
        const body = h.setup([{ duration: 4, stages: [{}, {}] }]);
        clickRegionStageChip(body, 0, 1, true);
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].stages).toHaveLength(1);
    });

    it("remaps IC-LoRA stage targets when a stage is deleted", () => {
        const body = h.setup([
            {
                duration: 4,
                stages: [{}, {}, {}],
                icLoras: [
                    { lora: "a", stage: 2 },
                    {
                        lora: "b",
                        stage: 1,
                        driveSource: "Incoming",
                        driveData: "visual",
                    },
                    { lora: "c", stage: 0 },
                    { lora: "d", stage: -1 },
                ],
            },
        ]);
        clickRegionStageChip(body, 0, 1, true);
        const clip = lastSavedClips<Clip[]>(h.saveSpy)[0];
        expect(clip.stages).toHaveLength(2);
        // Above the deleted stage shifts down; on it falls back to all stages.
        // Removing the supplying stage also repairs stale Incoming state.
        expect(clip.icLoras.map((e) => e.stage)).toEqual([1, -1, 0, -1]);
        expect(clip.icLoras[1].driveSource).toBe("Upload");
    });

    it("adds a stage from the rail's Add button and selects it", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        // Stage 0 is permanent, so it has no delete affordance.
        const del = document.querySelector<HTMLButtonElement>(
            ".vst-detail-delete-stage",
        );
        expect(del).toBeNull();
        const add = document.querySelector<HTMLElement>(
            ".vst-detail-add-stage",
        );
        expect(add?.textContent).toBe("+ Add Video Stage");
        add?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].stages).toHaveLength(2);
        expect(activeRailLabel()).toBe("S1");
        expect(crumbText()).toBe("Clip 0 · S1");
        expect(
            document.querySelector<HTMLButtonElement>(
                '.vst-stage-tab[aria-pressed="true"] .vst-detail-delete-stage',
            )?.disabled,
        ).toBe(false);
    });

    it("adds the first architecture stage to a zero-stage source-only clip", () => {
        h.setup([
            {
                duration: 4,
                initVideo: initVideoFixture({
                    fileName: "source.mp4",
                    durationSeconds: 4,
                    lengthSeconds: 4,
                }),
                stages: [],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const add = document.querySelector<HTMLButtonElement>(
            ".vst-detail-add-stage",
        );

        expect(add?.disabled).toBe(false);
        add?.click();

        expect(persistence.getClips()[0].stages).toHaveLength(1);
        expect(persistence.getClips()[0].architectureHint).not.toBe("none");
        expect(activeRailLabel()).toBe("S0");
    });

    it("omits skip and delete controls for the first clip and first stage", () => {
        h.setup([
            { duration: 4, stages: [{}, {}] },
            { duration: 4, stages: [{}, {}] },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        const clipSkip = document.querySelector<HTMLButtonElement>(
            ".vst-detail-skip-clip",
        );
        const clipDelete = document.querySelector<HTMLButtonElement>(
            ".vst-detail-delete-clip",
        );
        const stageSkip = document.querySelector<HTMLButtonElement>(
            '.vst-stage-tab[aria-pressed="true"] .vst-detail-skip-stage',
        );
        const stageDelete = document.querySelector<HTMLButtonElement>(
            '.vst-stage-tab[aria-pressed="true"] .vst-detail-delete-stage',
        );
        expect(clipSkip).toBeNull();
        expect(clipDelete).toBeNull();
        expect(stageSkip).toBeNull();
        expect(stageDelete).toBeNull();

        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        expect(document.querySelector(".vst-detail-skip-clip")).not.toBeNull();
        expect(
            document.querySelector(
                '.vst-stage-tab[aria-pressed="true"] .vst-detail-skip-stage',
            ),
        ).toBeNull();
        expect(
            document.querySelector(
                '.vst-stage-tab[aria-pressed="true"] .vst-detail-delete-stage',
            ),
        ).toBeNull();
    });

    it("shows adjacent skip/delete actions for Clip 1+ and selects a survivor after delete", () => {
        h.setup([
            { duration: 2, stages: [{}] },
            { duration: 3, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });

        const header = document.querySelector<HTMLElement>(
            ".vst-detail-clip-section > .input-group-header",
        );
        const skip = header?.querySelector<HTMLButtonElement>(
            ".vst-detail-skip-clip",
        );
        const remove = header?.querySelector<HTMLButtonElement>(
            ".vst-detail-delete-clip",
        );
        expect(skip).not.toBeNull();
        expect(remove).not.toBeNull();
        expect(skip?.parentElement).toBe(remove?.parentElement);
        expect(remove?.classList.contains("interrupt-button")).toBe(true);
        expect(remove?.textContent).toBe("×");
        expect(remove?.hasAttribute("aria-pressed")).toBe(false);

        remove?.click();

        expect(
            lastSavedClips<Clip[]>(h.saveSpy).map((clip) => clip.duration),
        ).toEqual([2, 4]);
        expect(crumbText()).toBe("Clip 1 · S0");
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 1,
            stageIdx: 0,
        });
    });

    it("mutes the stage params and persists Skip this stage", () => {
        h.setup([{ duration: 4, stages: [{}, {}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        const fields =
            document.querySelector<HTMLElement>(".vst-detail-fields");
        expect(fields?.classList.contains("vst-stage-fields-muted")).toBe(
            false,
        );
        document
            .querySelector<HTMLButtonElement>(
                '.vst-stage-tab[aria-pressed="true"] .vst-detail-skip-stage',
            )
            ?.click();
        expect(
            document
                .querySelector<HTMLElement>(".vst-detail-fields")
                ?.classList.contains("vst-stage-fields-muted"),
        ).toBe(true);
        expect(committedClips()[0].stages[1].skipped).toBe(true);
    });

    it("persists clip skip and restore cascades through the detail dock", () => {
        h.setup([
            { duration: 2, stages: [{}] },
            { duration: 3, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-skip-clip")
            ?.click();
        expect(committedClips().map((clip) => clip.skipped)).toEqual([
            false,
            true,
            true,
        ]);

        setSelection({ kind: "clip", clipIdx: 2, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-skip-clip")
            ?.click();
        expect(committedClips().map((clip) => clip.skipped)).toEqual([
            false,
            false,
            false,
        ]);
    });

    it("persists stage skip and restore cascades through the detail dock", () => {
        h.setup([{ duration: 4, stages: [{}, {}, {}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        document
            .querySelector<HTMLButtonElement>(
                '.vst-stage-tab[aria-pressed="true"] .vst-detail-skip-stage',
            )
            ?.click();
        expect(
            committedClips()[0].stages.map((stage) => stage.skipped),
        ).toEqual([false, true, true]);

        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 2 });
        document
            .querySelector<HTMLButtonElement>(
                '.vst-stage-tab[aria-pressed="true"] .vst-detail-skip-stage',
            )
            ?.click();
        expect(
            committedClips()[0].stages.map((stage) => stage.skipped),
        ).toEqual([false, false, false]);
    });

    describe("init-video clip stage 0 refine params", () => {
        const INIT_VIDEO = initVideoFixture({
            fileName: "clip.mp4",
            durationSeconds: 4,
            lengthSeconds: 4,
        });
        const fields = (): HTMLElement | null =>
            document.querySelector<HTMLElement>(".vst-detail-fields");
        const note = (): string =>
            document.querySelector(".vst-stage-passthrough-note")
                ?.textContent ?? "";

        it("renders enabled refine params and a footage note on init-video stage 0", () => {
            h.setup([
                {
                    duration: 4,
                    stages: [{ control: 0.5, upscale: 2 }, {}],
                    initVideo: INIT_VIDEO,
                },
            ]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            // InitVideo stage 0 refines its footage: no passthrough gating.
            expect(
                fields()?.classList.contains("vst-stage-fields-passthrough"),
            ).toBe(false);
            expect(note()).toContain("starts from the source footage");
            // The refine controls (Control / Upscale / Upscale Method) render
            // and are live — a generation stage 0 lacks them entirely.
            expect(sliderNumberByLabel("Control").disabled).toBe(false);
            expect(sliderNumberByLabel("Upscale").disabled).toBe(false);
            expect(
                fieldByLabel("Upscale Method").querySelector<HTMLSelectElement>(
                    "select",
                )?.disabled,
            ).toBe(false);

            // Later stages keep their live editors and no footage note.
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            expect(
                fields()?.classList.contains("vst-stage-fields-passthrough"),
            ).toBe(false);
            expect(note()).toBe("");
        });

        it("leaves stage 0 of an non-init-video clip without refine params or note", () => {
            h.setup([{ duration: 4, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            expect(
                fields()?.classList.contains("vst-stage-fields-passthrough"),
            ).toBe(false);
            expect(note()).toBe("");
            // Generation stage 0 forces Control/Upscale, so those widgets are absent.
            expect(() => sliderNumberByLabel("Control")).toThrow();
        });
    });

    it("commits a clip Duration edit through applyClipDurationResize", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const dur =
            fieldByLabel("Duration (s)").querySelector<HTMLInputElement>(
                "input",
            );
        if (!dur) {
            throw new Error("duration input missing");
        }
        dur.value = "6";
        dur.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].duration).toBe(6);
    });

    it("disables the Duration field when clip length is derived from audio", () => {
        h.setup([
            {
                duration: 4,
                audioSource: "Upload",
                clipLengthFromAudio: true,
                stages: [{}],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const field = fieldByLabel("Duration (s)");
        expect(field.querySelector<HTMLInputElement>("input")?.disabled).toBe(
            true,
        );
        expect(field.classList.contains("vst-field-disabled")).toBe(true);
    });

    it("reserves the derived-duration hint row while duration is editable", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        const hint = fieldByLabel("Duration (s)").querySelector<HTMLElement>(
            ".vst-detail-field-hint",
        );
        expect(hint?.textContent).toBe(
            "(derived from a reference's media length)",
        );
        expect(hint?.classList).toContain("vst-detail-field-hint-hidden");
    });

    it("deletes the current stage from the rail's Delete stage button", () => {
        h.setup([{ duration: 4, stages: [{}, {}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        const deleteBtn = document.querySelector<HTMLElement>(
            '.vst-stage-tab[aria-pressed="true"] .vst-detail-delete-stage',
        );
        expect(deleteBtn).not.toBeNull();
        deleteBtn?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].stages).toHaveLength(1);
    });

    it("replaces Clear/collapse with a gear modal and persists its toggles", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(detail()?.querySelector(".vst-detail-clear")).toBeNull();
        expect(detail()?.querySelector(".vst-detail-collapse")).toBeNull();
        const gear = detail()?.querySelector<HTMLButtonElement>(
            ".vst-detail-settings-button",
        );
        expect(gear?.getAttribute("aria-label")).toBe("Timeline settings");
        gear?.click();

        const modal = document.querySelector<HTMLElement>(
            ".vst-timeline-settings-modal",
        );
        expect(modal?.getAttribute("role")).toBe("dialog");
        const checks = modal?.querySelectorAll<HTMLInputElement>(
            'input[type="checkbox"]',
        );
        expect(checks).toHaveLength(2);
        expect(checks?.[0].checked).toBe(true);
        expect(checks?.[1].checked).toBe(true);
        if (checks?.[0]) {
            checks[0].checked = false;
            checks[0].dispatchEvent(new Event("change", { bubbles: true }));
        }
        expect(
            JSON.parse(
                localStorage.getItem(
                    "videostages.timeline.authoringSettings",
                ) ?? "{}",
            ),
        ).toMatchObject({ snap: false, autoCollapse: true });
    });

    it("degrades a removed clip's selection to the nearest surviving clip", () => {
        const body = h.setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        expect(crumbText()).toBe("Clip 1 · S0");

        const clips = persistence.getClips().slice(0, 1);
        persistence.saveClips(clips, { notifyDomChange: false });
        renderTimeline(body, persistence.getClips());
        h.strip.render();
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 0,
            stageIdx: 0,
        });
        expect(crumbText()).toBe("Clip 0 · S0");
    });

    it("drops the selection when the last clip is removed", () => {
        const body = h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        persistence.saveClips([], { notifyDomChange: false });
        renderTimeline(body, persistence.getClips());
        h.strip.render();
        expect(getSelection().kind).toBe("none");
        expect(crumbText()).toBe("Timeline settings");
    });

    it("adds and selects a reference from the clip sidebar rail", () => {
        h.setup([{ duration: 5, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-add-ref")
            ?.click();
        expect(committedClips()[0].frameRefs).toHaveLength(1);
        expect(getSelection()).toEqual({
            kind: "ref",
            clipIdx: 0,
            refIdx: 0,
        });
        expect(refRow(0)).not.toBeNull();
    });

    it("lists every ref in a rail and edits only the selected one", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}],
                frameRefs: [
                    { source: "Refiner", frame: 1 },
                    { source: "Base", frame: 2 },
                ],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 1 });
        expect(crumbText()).toBe("Keyframe 1 · Clip 0");
        expect(document.querySelectorAll(".vst-detail-ref-row")).toHaveLength(
            1,
        );
        expect(document.querySelectorAll(".vst-ref-tab")).toHaveLength(2);
        expect(
            refRow(1).classList.contains("vst-detail-repeating-editor-active"),
        ).toBe(true);

        // Edit is scoped to the selected ref's OWN row (index 1), not ref 0.
        const sourceSelect =
            refRow(1).querySelector<HTMLSelectElement>("select");
        if (!sourceSelect) {
            throw new Error("source select missing");
        }
        expect(Array.from(sourceSelect.options).map((o) => o.value)).toEqual([
            "Base",
            "Refiner",
            "Upload",
        ]);
        sourceSelect.value = "Upload";
        sourceSelect.dispatchEvent(new Event("change", { bubbles: true }));
        expect(h.saveSpy).toHaveBeenCalled();
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].frameRefs[1].source).toBe(
            "Upload",
        );
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].frameRefs[0].source).toBe(
            "Refiner",
        );

        // Rail delete removes the active ref, selects its neighbour, and
        // preserves the dock's scroll position.
        const beforeDelete = detailBody();
        if (!beforeDelete) {
            throw new Error("dock body missing");
        }
        beforeDelete.scrollTop = 140;
        document
            .querySelector<HTMLElement>(".vst-detail-delete-ref")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(committedClips()[0].frameRefs).toHaveLength(1);
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 0 });
        expect(detailBody()?.scrollTop).toBe(140);
        expect(refRow(0).dataset.vstRefIndex).toBe("0");
    });

    it("reveals References when an already-selected timeline ref is activated", () => {
        const original = Object.getOwnPropertyDescriptor(
            HTMLElement.prototype,
            "scrollIntoView",
        );
        const reveal = jest.fn();
        Object.defineProperty(HTMLElement.prototype, "scrollIntoView", {
            configurable: true,
            value: reveal,
        });
        try {
            h.setup([
                {
                    duration: 5,
                    stages: [{}],
                    frameRefs: [{ source: "Base", frame: 1 }],
                    icLoras: [
                        {
                            lora: "lora-x.safetensors",
                            driveData: "visual",
                        },
                    ],
                },
            ]);
            setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
            reveal.mockClear();
            activateSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
            expect(getSelection()).toEqual({
                kind: "ref",
                clipIdx: 0,
                refIdx: 0,
            });
            expect(reveal).toHaveBeenCalledTimes(1);
            expect(
                document.querySelector('[data-vst-repeater-key="references"]'),
            ).not.toBeNull();
        } finally {
            if (original) {
                Object.defineProperty(
                    HTMLElement.prototype,
                    "scrollIntoView",
                    original,
                );
            } else {
                Reflect.deleteProperty(HTMLElement.prototype, "scrollIntoView");
            }
        }
    });

    it("deleting the LAST ref falls back to the owning clip's panel", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}],
                frameRefs: [{ source: "Base", frame: 1 }],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        document
            .querySelector<HTMLElement>(".vst-detail-delete-ref")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(committedClips()[0].frameRefs).toHaveLength(0);
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 0,
            stageIdx: 0,
        });
    });

    it("switches the active reference editor from the reference rail", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}],
                frameRefs: [
                    { source: "Base", frame: 1 },
                    { source: "Refiner", frame: 2 },
                ],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        const before = refRow(0);
        document
            .querySelectorAll<HTMLButtonElement>(".vst-ref-tab")[1]
            ?.click();
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 1 });
        expect(refRow(1)).not.toBe(before);
        expect(
            refRow(1).classList.contains("vst-detail-repeating-editor-active"),
        ).toBe(true);
    });

    it("clamps an edited ref frame and writes it through saveClips", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}],
                frameRefs: [{ source: "Base", frame: 1 }],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        jest.useFakeTimers();
        const frameRow = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .vst-detail-field",
            ),
        ).find((r) =>
            r
                .querySelector(".vst-detail-field-label")
                ?.textContent?.startsWith("Attach at Frame"),
        );
        const input = frameRow?.querySelector<HTMLInputElement>("input");
        if (!input) {
            throw new Error("frame input missing");
        }
        input.value = "7";
        input.dispatchEvent(new Event("change", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].frameRefs[0].frame).toBe(7);
    });

    it("renders the audio editor and live-applies source + flags", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}, {}, {}],
                icLoras: [
                    {
                        lora: "some-lora",
                        driveSource: "ControlNet 1",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "audio", clipIdx: 0 });
        expect(crumbText()).toBe("Audio · Clip 0");
        const select =
            fieldByLabel("Audio Source").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!select) {
            throw new Error("audio source select missing");
        }
        expect(Array.from(select.options).map((o) => o.value)).toContain(
            "ControlNet",
        );
        expect(
            fieldByLabel("Clip Length from Audio").querySelector("input")
                ?.disabled,
        ).toBe(true);

        const reuse = fieldByLabel(
            "Reuse Captured Stage Audio",
        ).querySelector<HTMLInputElement>("input");
        if (!reuse) {
            throw new Error("reuse checkbox missing");
        }
        reuse.checked = true;
        reuse.dispatchEvent(new Event("change", { bubbles: true }));
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].reuseAudio).toBe(true);

        select.value = "Upload";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].audioSource).toBe("Upload");
        expect(
            fieldByLabel("Clip Length from Audio").querySelector("input")
                ?.disabled,
        ).toBe(false);
    });

    it("trims uploaded base audio with the shared modal", () => {
        h.setup([
            {
                duration: 5,
                stages: [{}],
                audioSource: "Upload",
                uploadedAudio: {
                    data: "data:audio/wav;base64,AAAA",
                    fileName: "voice.wav",
                },
                uploadedAudioDurationSeconds: 8,
                uploadedAudioStartSeconds: 1,
                uploadedAudioLengthSeconds: 4,
            },
        ]);
        setSelection({ kind: "audio", clipIdx: 0 });

        expect(
            document.querySelector(".vst-sidebar-audio-preview")?.tagName,
        ).toBe("AUDIO");
        document
            .querySelector<HTMLButtonElement>(
                ".vst-detail-audio [data-vst-open-trim]",
            )
            ?.click();

        expect(document.querySelector(".vst-trim-modal-player")?.tagName).toBe(
            "AUDIO",
        );
        const inInput = document.querySelector<HTMLInputElement>(
            '[data-vst-trim-field="in"]',
        );
        if (!inInput) {
            throw new Error("base audio trim input missing");
        }
        inInput.value = "2";
        inInput.dispatchEvent(new Event("input", { bubbles: true }));
        document
            .querySelector<HTMLButtonElement>("[data-vst-trim-apply]")
            ?.click();

        expect(persistence.getState().clips[0]).toMatchObject({
            uploadedAudioStartSeconds: 2,
            uploadedAudioLengthSeconds: 3,
        });
        const preview = document.querySelector<HTMLAudioElement>(
            ".vst-sidebar-audio-preview",
        );
        preview?.dispatchEvent(new Event("loadedmetadata"));
        expect(preview?.currentTime).toBe(2);
    });

    it("offers captured-stage audio reuse below three active stages", () => {
        h.setup([{ duration: 5, stages: [{}, {}, { skipped: true }] }]);
        setSelection({ kind: "audio", clipIdx: 0 });
        const row = fieldByLabel("Reuse Captured Stage Audio");
        const reuse = row.querySelector<HTMLInputElement>("input");
        // Too few stages to actually reuse; the backend drops it silently rather
        // than the control refusing the authoring.
        expect(reuse?.disabled).toBe(false);
        expect(row.textContent).toContain("second active stage");
        expect(row.textContent).toContain("third active stage");
    });

    it("shows + Add segment in the audio editor and creates+selects a segment", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "audio", clipIdx: 0 });
        const addBtn = document.querySelector<HTMLElement>(
            ".vst-audio-track-add",
        );
        expect(addBtn).not.toBeNull();
        expect(addBtn?.textContent).toBe("+ Add Audio Track");
        addBtn?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(getSelection()).toEqual({
            kind: "audio-track",
            trackIdx: 0,
        });
        const segments = persistence.getState().audioTracks ?? [];
        expect(segments).toHaveLength(1);
        expect(segments[0].spans[0].timelineStartSeconds).toBe(0);
        expect(segments[0].spans[0].timelineLengthSeconds).toBe(2);
        expect(segments[0].volume).toBe(1);
        expect(segments[0].source.uploadedAudio).toBeNull();
    });

    it("filters timeline-wide segments to the selected clip audio window", () => {
        h.setup([
            { duration: 3, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        const state = persistence.getState();
        state.audioTracks = [
            {
                id: "track-clip-0",
                volume: 1,
                source: {
                    kind: "Upload",
                    reference: "",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-clip-0",
                        timelineStartSeconds: 0,
                        timelineLengthSeconds: 1,
                        sourceStartSeconds: 0,
                    },
                ],
            },
            {
                id: "track-both",
                volume: 1,
                source: {
                    kind: "Upload",
                    reference: "",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-both",
                        timelineStartSeconds: 2,
                        timelineLengthSeconds: 2,
                        sourceStartSeconds: 0,
                    },
                ],
            },
            {
                id: "track-clip-1",
                volume: 1,
                source: {
                    kind: "Upload",
                    reference: "",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-clip-1",
                        timelineStartSeconds: 3,
                        timelineLengthSeconds: 1,
                        sourceStartSeconds: 0,
                    },
                ],
            },
        ];
        persistence.saveState(state, { notifyDomChange: false });
        const visibleSegmentLabels = (): string[] =>
            Array.from(
                document.querySelectorAll<HTMLElement>(
                    ".vst-audio-track-tab .header-label",
                ),
            ).map((label) => label.textContent ?? "");

        setSelection({ kind: "audio", clipIdx: 0 });
        expect(visibleSegmentLabels()).toEqual(["A1", "A2"]);

        setSelection({ kind: "audio", clipIdx: 1 });
        expect(visibleSegmentLabels()).toEqual(["A2", "A3"]);
    });

    it("hovering a Reference Strength row highlights that ref's timeline mark", () => {
        const body = h.setup([
            {
                duration: 4,
                stages: [{}],
                frameRefs: [
                    { source: "Base", frame: 1 },
                    { source: "Refiner", frame: 12 },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const rows = document.querySelectorAll<HTMLElement>(
            ".vst-detail .vst-stage-ref-slider",
        );
        expect(rows).toHaveLength(2);
        const mark = body.querySelector<HTMLElement>(
            '.vst-refs-mark[data-clip-idx="0"][data-ref-idx="1"]',
        );
        if (!mark) {
            throw new Error("ref mark missing");
        }
        rows[1].dispatchEvent(new MouseEvent("mouseenter"));
        expect(mark.classList.contains("vst-ref-hover")).toBe(true);
        expect(
            body
                .querySelector('.vst-refs-mark[data-ref-idx="0"]')
                ?.classList.contains("vst-ref-hover"),
        ).toBe(false);
        rows[1].dispatchEvent(new MouseEvent("mouseleave"));
        expect(mark.classList.contains("vst-ref-hover")).toBe(false);
    });

    describe("dock groups & collapse", () => {
        it("keeps Clip fields visible above progressive native accordion sections", () => {
            h.setup([
                {
                    duration: 4,
                    stages: [{}, {}, {}],
                    initVideo: RETAKE_SOURCE,
                },
            ]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });

            const clipCol = detailBody()?.querySelector(".vst-detail-clip");
            expect(clipCol).not.toBeNull();
            expect(
                clipCol?.closest('[data-vst-accordion-key="clip"]'),
            ).toBeNull();
            expect(clipCol?.classList.contains("input-group-content")).toBe(
                true,
            );
            expect(
                clipCol
                    ?.closest(".vst-detail-clip-section")
                    ?.querySelector(".vst-detail-skip-clip"),
            ).toBeNull();
            const clipSection = clipCol?.closest<HTMLElement>(
                '[data-vst-static-key="clip"]',
            );
            expect(clipSection?.classList.contains("input-group")).toBe(true);
            expect(clipSection?.classList.contains("input-group-open")).toBe(
                true,
            );
            expect(
                clipSection?.querySelector(
                    ":scope > .input-group-header.input-group-shrinkable",
                ),
            ).toBeNull();

            const stagesSection = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="stages"]',
            );
            expect(stagesSection).not.toBeNull();
            expect(stagesSection?.classList.contains("input-group")).toBe(true);
            expect(stagesSection?.classList.contains("input-group-open")).toBe(
                true,
            );
            expect(
                stagesSection?.querySelector(
                    ":scope > .input-group-header .header-label",
                )?.textContent,
            ).toBe("Stages");
            expect(
                stagesSection?.querySelectorAll(
                    ":scope > .input-group-content > .vst-detail-repeating-group",
                ),
            ).toHaveLength(3);
            expect(
                stagesSection?.querySelector(
                    ".vst-detail-repeating-group .vst-detail-params",
                ),
            ).not.toBeNull();
            expect(
                detailBody()?.querySelector(".vst-detail-loras-section"),
            ).not.toBeNull();

            const retakeSec = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-accordion-key="retake"]',
            );
            expect(retakeSec).not.toBeNull();
            expect(
                retakeSec?.querySelector(
                    ":scope > .input-group-header .header-label",
                )?.lastChild?.textContent,
            ).toBe("Retake");
            expect(
                retakeSec?.querySelector(".info-popover-button"),
            ).not.toBeNull();
            expect(
                detailBody()?.querySelector(".vst-detail-add-retake"),
            ).not.toBeNull();
        });

        it("lists references above IC-LoRAs using the shared selector rails", () => {
            h.setup([
                {
                    duration: 10,
                    stages: [{}],
                    frameRefs: [
                        { source: "Base", frame: 1 },
                        { source: "Refiner", frame: 8 },
                    ],
                    icLoras: [{ lora: "a" }, { lora: "b" }],
                },
            ]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const body = detailBody();
            const refsHead = body?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="references"]',
            );
            const icLorasHead = body?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="ic-loras"]',
            );
            expect(refsHead).not.toBeNull();
            expect(icLorasHead).not.toBeNull();
            expect(
                refsHead && icLorasHead
                    ? refsHead.compareDocumentPosition(icLorasHead) &
                          Node.DOCUMENT_POSITION_FOLLOWING
                    : 0,
            ).toBeTruthy();
            expect(body?.querySelectorAll(".vst-ref-tab")).toHaveLength(2);
            expect(
                refsHead?.querySelector<HTMLElement>(
                    ":scope > .input-group-header .header-label",
                )?.textContent,
            ).toBe("Keyframes");
            expect(
                body?.querySelector(".vst-detail-add-ref")?.textContent,
            ).toBe("+ Add Keyframe");
            expect(body?.textContent).toContain("Keyframe Strengths");
            expect(
                body?.querySelector(".vst-detail-delete-ref")?.textContent,
            ).toBe("×");
            expect(body?.querySelectorAll(".vst-iclora-tab")).toHaveLength(2);
            expect(body?.querySelectorAll(".vst-detail-iclora")).toHaveLength(
                1,
            );
            expect(
                body?.querySelector(".vst-detail-add-iclora")?.textContent,
            ).toBe("+ Add IC-LoRA");
            expect(
                body?.querySelector(".vst-detail-delete-iclora")?.textContent,
            ).toBe("×");

            const itemStructure = (
                section: Element | null | undefined,
            ): string[][] =>
                Array.from(
                    section?.querySelectorAll(
                        ":scope > .input-group-content > .vst-detail-repeating-group",
                    ) ?? [],
                ).map((item) =>
                    Array.from(item.children).map((child) =>
                        child.classList.contains("input-group-header")
                            ? "header"
                            : child.classList.contains("input-group-content")
                              ? "content"
                              : "other",
                    ),
                );
            expect(itemStructure(refsHead)).toEqual([
                ["header", "content"],
                ["header", "content"],
            ]);
            expect(itemStructure(icLorasHead)).toEqual(itemStructure(refsHead));

            setSelection({ kind: "ic-lora", clipIdx: 0, entryIdx: 1 });
            expect(
                detailBody()
                    ?.querySelector(".vst-detail-iclora")
                    ?.getAttribute("data-vst-iclora-idx"),
            ).toBe("1");
            const firstIcHeader = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="ic-loras"] [data-vst-repeater-item="0"] > .input-group-header',
            );
            firstIcHeader?.click();
            expect(
                detailBody()
                    ?.querySelector(".vst-detail-iclora")
                    ?.getAttribute("data-vst-iclora-idx"),
            ).toBe("0");
            expect(
                detailBody()
                    ?.querySelector(
                        '[data-vst-repeater-key="ic-loras"] [data-vst-repeater-item="0"]',
                    )
                    ?.classList.contains("input-group-open"),
            ).toBe(true);
        });

        it("adds references at unique rounded ten-percent frame intervals before wrapping", () => {
            h.setup([{ duration: 5, stages: [{}], frameRefs: [] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

            for (let index = 0; index < 12; index++) {
                const add = document.querySelector<HTMLButtonElement>(
                    ".vst-detail-add-ref",
                );
                expect(add).not.toBeNull();
                add?.click();
            }

            const frames = committedClips()[0].frameRefs.map(
                (reference) => reference.frame,
            );
            expect(frames).toEqual([
                1, 13, 25, 37, 49, 61, 73, 85, 97, 109, 121, 2,
            ]);
            expect(new Set(frames).size).toBe(12);
        });

        it("uses local native groups without consulting host group cookies", () => {
            const globals = globalThis as unknown as {
                getCookie: (name: string) => string;
            };
            expect(typeof globals.getCookie).toBe("function");
            jest.spyOn(globals, "getCookie").mockImplementation(() => "closed");
            h.setup([{ duration: 4, stages: [{}] }]);
            const body = detailBody();
            expect(body?.querySelector(".vst-detail-settings")).not.toBeNull();
            const settings = body?.querySelector<HTMLElement>(
                '[data-vst-accordion-key="timeline-settings"]',
            );
            expect(settings?.classList.contains("input-group-open")).toBe(true);
            expect(globals.getCookie).not.toHaveBeenCalled();
        });

        it("keeps only one top-level section open at a time", () => {
            h.setup([{ duration: 4, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const hostDelegatedToggle = jest.fn();
            document.addEventListener("click", hostDelegatedToggle);
            const stages = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="stages"]',
            );
            const source = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-accordion-key="init-video"]',
            );
            expect(stages?.classList.contains("input-group-open")).toBe(true);
            source
                ?.querySelector<HTMLElement>(":scope > .input-group-header")
                ?.click();
            expect(source?.classList.contains("input-group-open")).toBe(true);
            expect(stages?.classList.contains("input-group-closed")).toBe(true);
            expect(
                source?.querySelector<HTMLElement>(
                    ":scope > .input-group-content",
                )?.hidden,
            ).toBe(false);
            expect(
                source
                    ?.querySelector<HTMLElement>(":scope > .input-group-header")
                    ?.getAttribute("aria-expanded"),
            ).toBe("true");
            expect(hostDelegatedToggle).not.toHaveBeenCalled();
            document.removeEventListener("click", hostDelegatedToggle);
        });

        it("keeps other sections open when Auto-collapse is disabled", () => {
            h.setup([{ duration: 4, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            detail()
                ?.querySelector<HTMLButtonElement>(
                    ".vst-detail-settings-button",
                )
                ?.click();
            const autoCollapse = Array.from(
                document.querySelectorAll<HTMLInputElement>(
                    ".vst-timeline-settings-modal input[type='checkbox']",
                ),
            ).find((input) => input.dataset.name === "Auto-collapse");
            if (!autoCollapse) {
                throw new Error("Auto-collapse setting missing");
            }
            autoCollapse.checked = false;
            autoCollapse.dispatchEvent(new Event("change", { bubbles: true }));
            document
                .querySelector<HTMLButtonElement>(
                    ".vst-timeline-settings-modal .modal-header button",
                )
                ?.click();

            const stages = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="stages"]',
            );
            const source = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-accordion-key="init-video"]',
            );
            source
                ?.querySelector<HTMLElement>(":scope > .input-group-header")
                ?.click();
            expect(source?.classList.contains("input-group-open")).toBe(true);
            expect(stages?.classList.contains("input-group-open")).toBe(true);

            h.strip.render();
            expect(
                detailBody()
                    ?.querySelector('[data-vst-repeater-key="stages"]')
                    ?.classList.contains("input-group-open"),
            ).toBe(true);
            expect(
                detailBody()
                    ?.querySelector('[data-vst-accordion-key="init-video"]')
                    ?.classList.contains("input-group-open"),
            ).toBe(true);
        });

        it("keeps previously selected repeating item editors open when Auto-collapse is disabled", () => {
            h.setup([{ duration: 4, stages: [{}, {}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            detail()
                ?.querySelector<HTMLButtonElement>(
                    ".vst-detail-settings-button",
                )
                ?.click();
            const autoCollapse = Array.from(
                document.querySelectorAll<HTMLInputElement>(
                    ".vst-timeline-settings-modal input[type='checkbox']",
                ),
            ).find((input) => input.dataset.name === "Auto-collapse");
            if (!autoCollapse) {
                throw new Error("Auto-collapse setting missing");
            }
            autoCollapse.checked = false;
            autoCollapse.dispatchEvent(new Event("change", { bubbles: true }));
            document
                .querySelector<HTMLButtonElement>(
                    ".vst-timeline-settings-modal .modal-header button",
                )
                ?.click();

            detailBody()
                ?.querySelectorAll<HTMLElement>(
                    '[data-vst-repeater-key="stages"] > .input-group-content > .vst-detail-repeating-group > .input-group-header',
                )[1]
                ?.click();

            const stageGroups = (): HTMLElement[] =>
                Array.from(
                    detailBody()?.querySelectorAll<HTMLElement>(
                        '[data-vst-repeater-key="stages"] > .input-group-content > .vst-detail-repeating-group',
                    ) ?? [],
                );
            expect(
                stageGroups().every((group) =>
                    group.classList.contains("input-group-open"),
                ),
            ).toBe(true);
            expect(
                detailBody()?.querySelectorAll(
                    '[data-vst-repeater-key="stages"] .vst-detail-params',
                ),
            ).toHaveLength(2);

            h.strip.render();
            expect(
                stageGroups().every((group) =>
                    group.classList.contains("input-group-open"),
                ),
            ).toBe(true);
            expect(
                detailBody()?.querySelectorAll(
                    '[data-vst-repeater-key="stages"] .vst-detail-params',
                ),
            ).toHaveLength(2);
        });

        it("places every info popover button before its field or section label", () => {
            h.setup([{ duration: 4, stages: [{}, {}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            const buttons = Array.from(
                detailBody()?.querySelectorAll<HTMLElement>(
                    ".info-popover-button",
                ) ?? [],
            );
            expect(buttons.length).toBeGreaterThan(0);
            for (const button of buttons) {
                expect(button.parentElement?.firstElementChild).toBe(button);
            }
        });

        it("keeps the permanent Clip fields visible when its skip button changes", () => {
            h.setup([
                { duration: 4, stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
            const clip =
                detailBody()?.querySelector<HTMLElement>(".vst-detail-clip");
            expect(clip).not.toBeNull();
            expect(
                detailBody()?.querySelector('[data-vst-accordion-key="clip"]'),
            ).toBeNull();
            const skip = detailBody()?.querySelector<HTMLButtonElement>(
                ".vst-detail-clip-section > .input-group-header .vst-detail-skip-clip",
            );
            expect(skip?.getAttribute("aria-pressed")).toBe("false");
            skip?.click();

            expect(committedClips()[1].skipped).toBe(true);
            expect(
                detailBody()?.querySelector(".vst-detail-clip"),
            ).not.toBeNull();
            expect(
                detailBody()
                    ?.querySelector(".vst-detail-skip-clip")
                    ?.getAttribute("aria-pressed"),
            ).toBe("true");
            expect(
                detailBody()?.classList.contains("vst-detail-clip-skipped"),
            ).toBe(true);
        });
    });

    describe("scroll + targeted updates", () => {
        it("preserves dock-body scrollTop across a value-change render", () => {
            h.setup([{ duration: 4, stages: [{}, {}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const body = detailBody();
            if (!body) {
                throw new Error("dock body missing");
            }
            body.scrollTop = 140;
            // A full re-render rebuilds .vst-detail-body's innerHTML.
            h.strip.render();
            const rebuilt = detailBody();
            expect(rebuilt).not.toBe(body); // proves a rebuild happened
            expect(rebuilt?.scrollTop).toBe(140); // ...yet scroll is preserved
        });

        it("rebuilds the selected relay editor when its rail tab changes", () => {
            h.setup([
                {
                    duration: 12,
                    stages: [{}],
                    windows: [
                        { start: 1, duration: 2, prompt: "hello world" },
                        { start: 4, duration: 2, prompt: "second window" },
                    ],
                },
            ]);
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            const e0Before = minorEditor(0);
            document
                .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
                ?.click();
            expect(getSelection()).toEqual({
                kind: "prompt-minor",
                clipIdx: 0,
                windowIdx: 1,
            });
            expect(minorEditor(1)).not.toBe(e0Before);
            expect(document.activeElement).toBe(minorEditor(1));
            expect(minorRows()[0].dataset.vstMinorWindow).toBe("1");
        });
    });
});
