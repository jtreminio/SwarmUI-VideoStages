import { describe, expect, it } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCatalog,
    testArchitectureCatalogDto,
} from "../__test_helpers__/architectureFixtures";
import { lastSavedClips } from "../__test_helpers__/dom";
import {
    editorFieldContainer,
    editorHarness,
} from "../__test_helpers__/editor";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import { AUDIO_SOURCE_PREVIOUS_CLIP } from "../audioSource";
import { MEDIA_SOURCE_PREVIOUS_CLIP } from "../generatedMediaSource";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import * as persistence from "../persistence/repository";
import { setSelection } from "../selection";
import type { Clip } from "../types";

describe("production editor boundary panel", () => {
    const h = editorHarness();
    const fieldByLabel = (label: string): HTMLElement =>
        editorFieldContainer("join", label);
    const detailBody = (): HTMLElement => h.section("join");

    const boundarySelect = (): HTMLSelectElement => {
        const select = detailBody()?.querySelector<HTMLSelectElement>("select");
        if (!select) {
            throw new Error("boundary join select missing");
        }
        return select;
    };
    const infoText = (): string =>
        detailBody()?.querySelector<HTMLElement>(".vst-boundary-info")
            ?.textContent ?? "";
    const overlapSelect = (): HTMLSelectElement | null => {
        const fields = Array.from(
            detailBody()?.querySelectorAll<HTMLElement>(".vst-detail-field") ??
                [],
        );
        const field = fields.find(
            (f) =>
                f.querySelector(".vst-detail-field-label")?.textContent ===
                "Overlap",
        );
        return field?.querySelector<HTMLSelectElement>("select") ?? null;
    };
    const carryAudioCheckbox = (): HTMLInputElement | null => {
        const rows = Array.from(
            detailBody()?.querySelectorAll<HTMLElement>(
                ".vst-detail-field-check",
            ) ?? [],
        );
        const row = rows.find((candidate) =>
            candidate.textContent?.includes(
                "Continue outgoing audio into next clip",
            ),
        );
        return (
            row?.querySelector<HTMLInputElement>('input[type="checkbox"]') ??
            null
        );
    };

    it("renders the join section and available join modes", () => {
        h.setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });
        expect(h.section("join").querySelector("h3")?.textContent).toBe(
            "Join from previous clip",
        );
        expect(boundarySelect().value).toBe("cut");
        expect(
            Array.from(
                boundarySelect().options,
                (option) => option.textContent,
            ),
        ).toEqual(
            expect.arrayContaining(["Cut │", "Continue →", "Crossfade ⤬"]),
        );
    });

    it("live-applies the join mode through saveClips", () => {
        h.setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });
        const select = boundarySelect();
        select.value = "crossfade";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(h.saveSpy).toHaveBeenCalled();
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].boundaryOut).toBe(
            "crossfade",
        );
    });

    it("switches the pair between join and consumed source relationships", () => {
        h.setup([
            { duration: 4, stages: [{}, {}] },
            { duration: 6, stages: [{}], audioSource: "Native" },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });
        expect(
            Array.from(
                boundarySelect().options,
                (option) => option.textContent,
            ),
        ).toEqual(
            expect.arrayContaining(["As source video", "As source audio"]),
        );

        let select = boundarySelect();
        select.value = "source-video";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(persistence.getState().clips[1].initVideo).toMatchObject({
            source: MEDIA_SOURCE_PREVIOUS_CLIP,
            lengthSeconds: 4,
        });
        expect(boundarySelect().value).toBe("source-video");

        select = boundarySelect();
        select.value = "source-audio";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        const clips = persistence.getState().clips;
        expect(clips[0].audioOnly).toBe(true);
        expect(clips[0].stages.map((stage) => stage.skipped)).toEqual([
            false,
            true,
        ]);
        expect(clips[1].audioSource).toBe(AUDIO_SOURCE_PREVIOUS_CLIP);
        expect(clips[1].initVideo).toBeNull();
        expect(boundarySelect().value).toBe("source-audio");
    });

    it("selects source audio when Clip 0 is already audio-only", () => {
        h.setup([
            { duration: 4, stages: [{}], audioOnly: true },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });

        expect(boundarySelect().value).toBe("source-audio");
    });

    it("shows an Overlap selector and plan-aware info for a continue boundary", () => {
        h.setup([
            { duration: 4, boundaryOut: "continue", stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });
        // Default overlap 8 -> window 9 for ample clips.
        expect(infoText()).toContain("last 9 frames");
        expect(overlapSelect()).not.toBeNull();
        expect(overlapSelect()?.value).toBe("8");
    });

    it("shows tenths-rounded frame arithmetic for both clips and the shared join", () => {
        h.setup([
            {
                duration: 3,
                boundaryOut: "continue",
                boundaryOutOverlap: 24,
                stages: [{}],
            },
            { duration: 3, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });

        const impact =
            detailBody()?.querySelector<HTMLElement>(".vst-boundary-impact") ??
            null;
        expect(impact).not.toBeNull();
        expect(impact?.querySelector(".vst-detail-crumb")?.textContent).toBe(
            "Output impact",
        );
        expect(impact?.textContent).toContain("Clip 073f · 3.0s");
        expect(impact?.textContent).toContain("Clip 1+73f · +3.0s");
        expect(impact?.textContent).toContain(
            "Incoming Continue handle+24f · +1.0s",
        );
        expect(impact?.textContent).toContain("Continue shared−25f · −1.0s");
        expect(impact?.textContent).toContain(
            "Pair after this join145f · 6.0s",
        );
        expect(impact?.textContent).toContain(
            "24f selected + 1 LTX continuation frame",
        );
    });

    it("reports the normalized Continue selection in its arithmetic note", () => {
        h.setup([
            {
                duration: 3,
                boundaryOut: "continue",
                boundaryOutOverlap: 20,
                stages: [{}],
            },
            { duration: 3, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });

        expect(
            detailBody()?.querySelector(".vst-boundary-impact")?.textContent,
        ).toContain("16f selected + 1 LTX continuation frame = 17f effective");
    });

    it("commits a chosen overlap to boundaryOutOverlap", () => {
        h.setup([
            { duration: 4, boundaryOut: "continue", stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });
        const select = overlapSelect();
        if (!select) {
            throw new Error("overlap select missing");
        }
        select.value = "24";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].boundaryOutOverlap).toBe(
            24,
        );
    });

    it("offers opt-in outgoing audio carry for an overlapped boundary", () => {
        h.setup([
            { duration: 4, boundaryOut: "continue", stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });
        const checkbox = carryAudioCheckbox();
        expect(checkbox).not.toBeNull();
        expect(checkbox?.checked).toBe(false);

        if (!checkbox) {
            throw new Error("boundary audio carry checkbox missing");
        }
        checkbox.checked = true;
        checkbox.dispatchEvent(new Event("change", { bubbles: true }));

        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].boundaryOutCarryAudio).toBe(
            true,
        );
        expect(infoText()).toContain(
            "audio tail becomes preserved opening context",
        );
    });

    it("labels reference Continue as requested context and hides overlap audio carry", async () => {
        const catalog = testArchitectureCatalog();
        const constraints =
            catalog.architectures[0].boundaryRules.continue.constraints;
        if (!constraints) {
            throw new Error("continue constraints missing");
        }
        catalog.architectures[0].capabilities.features.push("clipReferences");
        constraints.continueMode = "reference";
        constraints.continuityExtraFrames = 0;
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(catalog),
        });
        await loadAuthoritativeArchitectureCatalog();

        h.setup([
            { duration: 4, boundaryOut: "continue", stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });

        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        h.editor.refresh();
        expect(fieldByLabel("Reference window")).not.toBeNull();
        expect(overlapSelect()).toBeNull();
        expect(carryAudioCheckbox()).toBeNull();
        expect(infoText()).toContain("requests up to ~");
        expect(infoText()).toContain("as reference context");
        expect(infoText()).not.toContain("receives this clip's last");
    });

    it("hides audio carry when the architecture does not support it", async () => {
        const catalog = testArchitectureCatalog();
        catalog.architectures[0].capabilities.features =
            catalog.architectures[0].capabilities.features.filter(
                (feature) => feature !== "audioBoundaryCarry",
            );
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(catalog),
        });
        await loadAuthoritativeArchitectureCatalog();

        h.setup([
            { duration: 4, boundaryOut: "continue", stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });

        expect(overlapSelect()).not.toBeNull();
        expect(carryAudioCheckbox()).toBeNull();
    });

    it("does not offer source audio when Clip 0 cannot generate audio", async () => {
        const catalog = testArchitectureCatalog();
        catalog.architectures[0].capabilities.audioSourceKinds = ["Disabled"];
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(catalog),
        });
        await loadAuthoritativeArchitectureCatalog();
        h.setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });

        expect(
            Array.from(
                boundarySelect().options,
                (option) => option.textContent,
            ),
        ).not.toContain("As source audio");
    });

    it("offers source audio only for the Clip 0 boundary", () => {
        h.setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 1 });

        expect(
            Array.from(
                boundarySelect().options,
                (option) => option.textContent,
            ),
        ).not.toContain("As source audio");
    });

    it("disables audio continuation when the next clip has no generation stage", () => {
        h.setup([
            { duration: 4, boundaryOut: "crossfade", stages: [{}] },
            { duration: 4, stages: [] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });

        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        expect(carryAudioCheckbox()?.disabled).toBe(true);
    });

    it("shows an Overlap selector and dissolve info for a crossfade boundary", () => {
        h.setup([
            { duration: 4, boundaryOut: "crossfade", stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });
        expect(overlapSelect()).not.toBeNull();
        expect(overlapSelect()?.value).toBe("8");
        expect(detailBody()?.querySelector(".vst-boundary-note")).toBeNull();
        expect(infoText()).toContain("8 frames");
    });

    it("shows no Overlap selector and no LTX-2 note for a cut boundary", () => {
        h.setup([
            { duration: 4, boundaryOut: "cut", stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });
        expect(overlapSelect()).toBeNull();
        expect(carryAudioCheckbox()).toBeNull();
        expect(detailBody()?.querySelector(".vst-boundary-note")).toBeNull();
    });

    it("returns to the remaining clip when a join loses its right clip", () => {
        h.setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "boundary", leftClipIdx: 0 });
        expect(h.section("join").querySelector("h3")?.textContent).toBe(
            "Join from previous clip",
        );
        const clips = persistence.getClips();
        clips.splice(1, 1);
        persistence.saveClips(clips);
        h.editor.refresh();
        expect(h.section("prompt").querySelector("textarea")?.value).toBe("");
        expect(h.root.querySelectorAll("[data-testid=clip-card]")).toHaveLength(
            1,
        );
    });
});
