import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    testArchitectureCapabilities,
    testArchitectureCatalog,
    testAuthoringTransactionSnapshot,
} from "../__test_helpers__/architectureFixtures";
import { mountPromptBox, mountVideoStagesData } from "../__test_helpers__/dom";
import { stubAceStepFunRegistry } from "../__test_helpers__/registries";
import type { ArchitectureModelCatalog } from "../architectures/types";
import { resetRememberedAccordionSections } from "../detailWidgets";
import { setVideoStagesHostBridgeForTests } from "../host";
import {
    __resetPersistenceForTests,
    getClips,
    getState,
} from "../persistence/repository";
import type { Clip, ClipReference } from "../types";
import { buildClipReferenceSection } from "./clipReferencePanel";
import type { DetailStripContext } from "./context";
import { clampDetailSelection, detailBreadcrumb } from "./panelRouter";
import { closeTrimModal } from "./trimModal";

const catalogSupportingClipReferences = (): ArchitectureModelCatalog => {
    const catalog = testArchitectureCatalog();
    catalog.architectures[0].capabilities = testArchitectureCapabilities({
        features: [
            ...testArchitectureCapabilities().features,
            "clipReferences",
        ],
    });
    return catalog;
};

const buildBody = (
    references: Partial<ClipReference>[],
    selectedIdx: number,
    catalog = catalogSupportingClipReferences(),
    context: Partial<DetailStripContext> = {},
): HTMLElement => {
    mountVideoStagesData({
        clips: [
            {
                duration: 5,
                stages: [{ model: "ltx-2.3.safetensors" }],
                references,
            },
        ],
    });
    return buildClipReferenceSection(
        {
            authoring: () => testAuthoringTransactionSnapshot(catalog),
            ...context,
        } as unknown as DetailStripContext,
        0,
        selectedIdx,
        getClips(),
        getState().fps,
    );
};

/** `buildCheckbox` names its input after the label it renders beside. */
const checkbox = (body: HTMLElement, label: string): HTMLInputElement | null =>
    body.querySelector<HTMLInputElement>(`input[data-name^="${label}"]`);

/** The repeater wraps each tab label in its own disclosure and delete glyphs. */
const tabLabels = (body: HTMLElement): string[] =>
    Array.from(body.querySelectorAll(".vst-clip-ref-tab")).map(
        (tab) => tab.textContent?.replace(/^[⮟⮞]|×$/g, "") ?? "",
    );

describe("buildClipReferenceSection", () => {
    beforeEach(() => {
        localStorage.clear();
        __resetPersistenceForTests();
        resetRememberedAccordionSections();
        document.body.innerHTML = "";
        mountPromptBox("");
    });

    afterEach(() => {
        closeTrimModal();
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests(null);
        document.body.innerHTML = "";
    });

    it("labels each reference with the prompt tag the model presents it under", () => {
        const body = buildBody(
            [
                { kind: "image" },
                { kind: "video" },
                { kind: "image" },
                { kind: "audio" },
            ],
            0,
        );

        expect(tabLabels(body)).toEqual([
            "<Picture 1>",
            "<Video 1>",
            "<Picture 2>",
            "<Audio 1>",
        ]);
    });

    it("edits only scale and soundtrack on the non-deleteable incoming Continue reference", () => {
        const catalog = catalogSupportingClipReferences();
        const constraints =
            catalog.architectures[0].boundaryRules.continue.constraints;
        if (!constraints) {
            throw new Error("continue constraints missing");
        }
        constraints.continueMode = "reference";
        constraints.continuityExtraFrames = 0;
        mountVideoStagesData({
            clips: [
                {
                    duration: 5,
                    boundaryOut: "continue",
                    boundaryOutOverlap: 8,
                    stages: [{ model: "ltx-2.3.safetensors" }],
                    references: [],
                },
                {
                    duration: 5,
                    stages: [{ model: "ltx-2.3.safetensors" }],
                    references: [{ kind: "video" }, { kind: "audio" }],
                },
            ],
        });
        const clips = getClips();
        const render = jest.fn();
        const context = {
            authoring: () => testAuthoringTransactionSnapshot(catalog),
            commit: (mutate: (target: Clip[]) => void) => mutate(clips),
            render,
        } as unknown as DetailStripContext;
        const build = () =>
            buildClipReferenceSection(
                context,
                1,
                null,
                clips,
                getState().fps,
                true,
                true,
            );
        const body = build();

        expect(tabLabels(body)).toEqual([
            "<Video 1> (from Join with Clip 0)",
            "<Video 2>",
            "<Audio 2>",
        ]);
        const capabilities =
            testAuthoringTransactionSnapshot(catalog).capabilities;
        expect(
            detailBreadcrumb(
                { kind: "clip-ref", clipIdx: 1, referenceIdx: 0 },
                clips,
                getState().fps,
                capabilities,
            ),
        ).toBe("<Video 2> · Clip 1");
        expect(
            detailBreadcrumb(
                { kind: "clip-ref", clipIdx: 1, referenceIdx: 1 },
                clips,
                getState().fps,
                capabilities,
            ),
        ).toBe("<Audio 2> · Clip 1");
        const join = body.querySelector<HTMLElement>(".vst-clip-ref-join-tab");
        expect(join?.hasAttribute("aria-disabled")).toBe(false);
        expect(join?.tabIndex).toBe(0);
        const group = body
            .querySelector(".vst-clip-ref-join-tab")
            ?.closest(".vst-detail-repeating-group");
        expect(
            group?.querySelector<HTMLElement>(
                ".vst-detail-repeating-group-content",
            )?.hidden,
        ).toBe(false);
        expect(
            Array.from(
                group?.querySelectorAll(".vst-detail-field-label") ?? [],
            ).map((label) => label.textContent),
        ).toEqual(["Reference scale", "Include soundtrack"]);
        expect(
            group?.querySelector(".vst-detail-repeating-group-delete"),
        ).toBeNull();

        const scale = group?.querySelector<HTMLSelectElement>("select");
        if (!scale) {
            throw new Error("automatic reference scale missing");
        }
        scale.value = "0.5";
        scale.dispatchEvent(new Event("change", { bubbles: true }));
        const soundtrack = group?.querySelector<HTMLInputElement>(
            'input[type="checkbox"]',
        );
        if (!soundtrack) {
            throw new Error("automatic reference soundtrack toggle missing");
        }
        soundtrack.checked = false;
        soundtrack.dispatchEvent(new Event("change", { bubbles: true }));
        expect(clips[0].boundaryOutReferenceScale).toBe(0.5);
        expect(clips[0].boundaryOutReferenceIncludeSoundtrack).toBe(false);
        expect(render).toHaveBeenCalledTimes(1);
        expect(
            detailBreadcrumb(
                { kind: "clip-ref", clipIdx: 1, referenceIdx: 1 },
                clips,
                getState().fps,
                capabilities,
            ),
        ).toBe("<Audio 1> · Clip 1");
        const boundarySelection = {
            kind: "boundary-ref" as const,
            leftClipIdx: 0,
        };
        expect(
            clampDetailSelection(
                boundarySelection,
                clips,
                [],
                getState().fps,
                capabilities,
            ),
        ).toEqual(boundarySelection);
        clips[0].boundaryOut = "cut";
        expect(
            clampDetailSelection(
                boundarySelection,
                clips,
                [],
                getState().fps,
                capabilities,
            ),
        ).toEqual({ kind: "none" });
    });

    it("omits the join reference when the target has no generating stage", () => {
        const catalog = catalogSupportingClipReferences();
        const constraints =
            catalog.architectures[0].boundaryRules.continue.constraints;
        if (!constraints) {
            throw new Error("continue constraints missing");
        }
        constraints.continueMode = "reference";
        constraints.continuityExtraFrames = 0;
        constraints.targetRequiresGeneratedEntry = false;
        mountVideoStagesData({
            clips: [
                {
                    duration: 5,
                    boundaryOut: "continue",
                    boundaryOutOverlap: 8,
                    stages: [{ model: "ltx-2.3.safetensors" }],
                },
                {
                    duration: 5,
                    initVideo: {
                        data: "data:video/mp4;base64,AA==",
                        fileName: "source.mp4",
                        fps: 24,
                        durationSeconds: 5,
                        startSeconds: 0,
                        lengthSeconds: 5,
                    },
                    stages: [{ model: "ltx-2.3.safetensors", control: 0 }],
                    references: [{ kind: "video" }],
                },
            ],
        });
        const clips = getClips();
        const body = buildClipReferenceSection(
            {
                authoring: () => testAuthoringTransactionSnapshot(catalog),
            } as unknown as DetailStripContext,
            1,
            0,
            clips,
            getState().fps,
            true,
        );

        expect(tabLabels(body)).toEqual(["<Video 1>"]);
        expect(body.querySelector(".vst-clip-ref-join-tab")).toBeNull();
    });

    it("offers a Source field with the sources supported by each reference kind", () => {
        stubAceStepFunRegistry(["audio0", "audio2"]);
        const sourceValues = (body: HTMLElement) => {
            const field = Array.from(
                body.querySelectorAll<HTMLElement>(".vst-detail-field"),
            ).find(
                (candidate) =>
                    candidate.querySelector(".vst-detail-field-label")
                        ?.textContent === "Source",
            );
            return Array.from(
                field?.querySelectorAll<HTMLOptionElement>("option") ?? [],
            ).map((option) => option.value);
        };

        expect(sourceValues(buildBody([{ kind: "image" }], 0))).toEqual([
            "Base",
            "Refiner",
            "Upload",
            "ControlNet 1",
            "ControlNet 2",
            "ControlNet 3",
        ]);
        expect(sourceValues(buildBody([{ kind: "video" }], 0))).toEqual([
            "Upload",
            "ControlNet 1",
            "ControlNet 2",
            "ControlNet 3",
        ]);
        expect(sourceValues(buildBody([{ kind: "audio" }], 0))).toEqual([
            "Upload",
            "ControlNet 1",
            "ControlNet 2",
            "ControlNet 3",
            "audio0",
            "audio2",
        ]);
    });

    it("shows the soundtrack toggle only on a video reference", () => {
        const hasToggle = (body: HTMLElement) =>
            Array.from(body.querySelectorAll("label")).some((el) =>
                el.textContent?.includes("Include soundtrack"),
            );

        expect(hasToggle(buildBody([{ kind: "video" }], 0))).toBe(true);
        expect(hasToggle(buildBody([{ kind: "image" }], 0))).toBe(false);
    });

    describe("trim", () => {
        const videoReference = (
            overrides: Partial<ClipReference> = {},
        ): Partial<ClipReference> => ({
            kind: "video",
            mediaDurationSeconds: 8,
            uploadedMedia: {
                data: "data:video/mp4;base64,AAAA",
                fileName: "reference.mp4",
            },
            ...overrides,
        });
        const openTrim = (body: HTMLElement): void => {
            const launch = body.querySelector<HTMLButtonElement>(
                "[data-vst-open-trim]",
            );
            if (!launch) {
                throw new Error("trim launcher missing");
            }
            launch.click();
            const player = document.querySelector<HTMLMediaElement>(
                ".vst-trim-modal-player",
            );
            if (player) {
                player.pause = jest.fn();
                player.load = jest.fn();
            }
        };
        const modalWindow = (): HTMLElement => {
            const window = document.querySelector<HTMLElement>(
                ".vst-trim-modal .vst-trim-window",
            );
            if (!window) {
                throw new Error("modal trim window missing");
            }
            return window;
        };

        const sidebarField = (
            body: HTMLElement,
            label: string,
        ): HTMLInputElement => {
            const row = Array.from(
                body.querySelectorAll<HTMLElement>(".vst-detail-field"),
            ).find(
                (candidate) =>
                    candidate.querySelector(".vst-detail-field-label")
                        ?.textContent === label,
            );
            const input = row?.querySelector<HTMLInputElement>("input");
            if (!input) {
                throw new Error(`${label} sidebar field missing`);
            }
            return input;
        };

        it("offers the modal editor on a video reference with a probed length", () => {
            const body = buildBody([videoReference()], 0);

            expect(body.querySelector("[data-vst-open-trim]")).not.toBeNull();
            expect(body.querySelector(".vst-trim")).toBeNull();
        });

        it("keeps editable In and Out fields in the reference sidebar", () => {
            let saved: Clip[] = [];
            const body = buildBody(
                [videoReference({ startSeconds: 2, lengthSeconds: 4 })],
                0,
                catalogSupportingClipReferences(),
                {
                    commit: (mutate) => {
                        saved = getClips();
                        mutate(saved);
                    },
                },
            );

            expect(sidebarField(body, "In (s)").value).toBe("2");
            expect(sidebarField(body, "Out (s)").value).toBe("6");

            const input = sidebarField(body, "Out (s)");
            input.value = "7";
            input.dispatchEvent(new Event("input", { bubbles: true }));
            expect(saved[0].references[0]).toMatchObject({
                startSeconds: 2,
                lengthSeconds: 5,
            });

            const preview = body.querySelector<HTMLVideoElement>(
                ".vst-sidebar-video-preview",
            );
            if (!preview) {
                throw new Error("reference preview missing");
            }
            preview.currentTime = 6.5;
            preview.dispatchEvent(new Event("seeking"));
            expect(preview.currentTime).toBe(6.5);
        });

        it("disables the sidebar fields while the video owns clip length", () => {
            const body = buildBody(
                [videoReference({ drivesClipLength: true })],
                0,
            );

            const inInput = sidebarField(body, "In (s)");
            const outInput = sidebarField(body, "Out (s)");
            expect(inInput.disabled).toBe(true);
            expect(outInput.disabled).toBe(true);
            expect(inInput.closest(".vst-detail-field")?.classList).toContain(
                "vst-field-disabled",
            );
            expect(outInput.closest(".vst-detail-field")?.classList).toContain(
                "vst-field-disabled",
            );
        });

        it("shows the selected reference video in the sidebar", () => {
            const body = buildBody([videoReference()], 0);

            const preview = body.querySelector<HTMLVideoElement>(
                ".vst-sidebar-video-preview",
            );
            expect(preview).not.toBeNull();
            expect(preview?.src).toContain("data:video/mp4");
        });

        /**
         * Without a probed length the bar has no truthful scale, and the
         * backend keeps the whole file regardless.
         */
        it("omits the editor when the reference length is unknown", () => {
            expect(
                buildBody(
                    [videoReference({ mediaDurationSeconds: 0 })],
                    0,
                ).querySelector("[data-vst-open-trim]"),
            ).toBeNull();
        });

        it("offers audio references the shared trim modal", () => {
            const body = buildBody(
                [
                    {
                        kind: "audio",
                        mediaDurationSeconds: 8,
                        uploadedMedia: {
                            data: "data:audio/wav;base64,AAAA",
                            fileName: "reference.wav",
                        },
                    },
                ],
                0,
            );

            expect(
                body.querySelector(".vst-sidebar-audio-preview")?.tagName,
            ).toBe("AUDIO");
            openTrim(body);

            expect(
                document.querySelector(".vst-trim-modal-player")?.tagName,
            ).toBe("AUDIO");
        });

        it("offers no trim on images", () => {
            expect(
                buildBody(
                    [{ kind: "image", mediaDurationSeconds: 8 }],
                    0,
                ).querySelector("[data-vst-open-trim]"),
            ).toBeNull();
        });

        /** An untrimmed reference stores 0/0, so the editor has to widen it. */
        it("shows an untrimmed reference as the whole file", () => {
            const body = buildBody(
                [
                    videoReference({
                        startSeconds: 0,
                        lengthSeconds: 0,
                    }),
                ],
                0,
            );
            openTrim(body);

            const window_ = modalWindow();
            expect(parseFloat(window_.style.left)).toBeCloseTo(0, 5);
            expect(parseFloat(window_.style.width)).toBeCloseTo(100, 5);
        });

        it("draws a stored trim over the part of the file it uses", () => {
            const body = buildBody(
                [
                    videoReference({
                        startSeconds: 2,
                        lengthSeconds: 4,
                    }),
                ],
                0,
            );
            openTrim(body);

            const window_ = modalWindow();
            expect(parseFloat(window_.style.left)).toBeCloseTo(25, 5);
            expect(parseFloat(window_.style.width)).toBeCloseTo(50, 5);
        });

        it("reports how much of the reference is used", () => {
            const body = buildBody(
                [
                    videoReference({
                        startSeconds: 2,
                        lengthSeconds: 4,
                    }),
                ],
                0,
            );

            expect(body.textContent).toContain("References 4.0 s of 8.0 s");
        });

        it("stores the modal range and rebuilds the reference editor on Apply", () => {
            let saved: Clip[] = [];
            const render = jest.fn();
            const body = buildBody(
                [
                    videoReference({
                        startSeconds: 2,
                        lengthSeconds: 4,
                    }),
                ],
                0,
                catalogSupportingClipReferences(),
                {
                    commit: (mutate) => {
                        saved = getClips();
                        mutate(saved);
                    },
                    render,
                },
            );
            openTrim(body);
            const input = document.querySelector<HTMLInputElement>(
                '[data-vst-trim-field="in"]',
            );
            if (!input) {
                throw new Error("modal In field missing");
            }
            input.value = "3";
            input.dispatchEvent(new Event("input", { bubbles: true }));
            document
                .querySelector<HTMLButtonElement>("[data-vst-trim-apply]")
                ?.click();

            expect(saved[0].references[0]).toMatchObject({
                startSeconds: 3,
                lengthSeconds: 3,
            });
            expect(render).toHaveBeenCalledTimes(1);
        });

        it("resizes a clip when its length-driving reference is trimmed", () => {
            let saved: Clip[] = [];
            const body = buildBody(
                [
                    videoReference({
                        startSeconds: 2,
                        lengthSeconds: 4,
                        drivesClipLength: true,
                    }),
                ],
                0,
                catalogSupportingClipReferences(),
                {
                    commit: (mutate) => {
                        saved = getClips();
                        mutate(saved);
                    },
                    render: jest.fn(),
                },
            );
            openTrim(body);
            const input = document.querySelector<HTMLInputElement>(
                '[data-vst-trim-field="in"]',
            );
            if (!input) {
                throw new Error("modal In field missing");
            }
            input.value = "3";
            input.dispatchEvent(new Event("input", { bubbles: true }));
            document
                .querySelector<HTMLButtonElement>("[data-vst-trim-apply]")
                ?.click();

            expect(saved[0].references[0]).toMatchObject({
                startSeconds: 3,
                lengthSeconds: 3,
            });
            expect(saved[0].duration).toBe(3);
        });
    });

    it("disables Add for an architecture that does not support clip references", () => {
        const add = buildBody(
            [],
            0,
            testArchitectureCatalog(),
        ).querySelector<HTMLButtonElement>(".vst-detail-add-clip-ref");

        expect(add?.disabled).toBe(true);
    });

    it("offers the length toggle only on timed media", () => {
        expect(
            checkbox(
                buildBody([{ kind: "video", mediaDurationSeconds: 4.5 }], 0),
                "Clip Length from Video",
            ),
        ).not.toBeNull();
        expect(
            checkbox(
                buildBody([{ kind: "audio", mediaDurationSeconds: 2 }], 0),
                "Clip Length from Audio",
            ),
        ).not.toBeNull();
        expect(
            checkbox(buildBody([{ kind: "image" }], 0), "Clip Length from"),
        ).toBeNull();
    });

    it("disables the toggle until the media reports a length", () => {
        const body = buildBody([{ kind: "video", mediaDurationSeconds: 0 }], 0);

        expect(checkbox(body, "Clip Length from Video")?.disabled).toBe(true);
        expect(body.textContent).toContain("Detected: unknown length");
    });

    it("offers the downsample scale only on a video reference", () => {
        const scaleField = (body: HTMLElement) =>
            Array.from(body.querySelectorAll(".vst-detail-field")).find((el) =>
                el
                    .querySelector(".vst-detail-field-label")
                    ?.textContent?.startsWith("Reference scale"),
            );

        const video = scaleField(
            buildBody([{ kind: "video", mediaScale: 0.25 }], 0),
        );
        expect(video?.querySelector("select")?.value).toBe("0.25");
        expect(scaleField(buildBody([{ kind: "audio" }], 0))).toBeUndefined();
        expect(scaleField(buildBody([{ kind: "image" }], 0))).toBeUndefined();
    });

    it("moves clip length ownership to the checked reference alone", () => {
        // One captured array, so the commit's mutation is what the assertions
        // read back — the repository hands out a fresh copy per call.
        let clips: Clip[] = [];
        const commit = jest.fn((mutate: (target: Clip[]) => void) => {
            mutate(clips);
        });
        const body = buildBody(
            [
                {
                    kind: "video",
                    mediaDurationSeconds: 9,
                    drivesClipLength: true,
                },
                { kind: "audio", mediaDurationSeconds: 4.5 },
            ],
            1,
            catalogSupportingClipReferences(),
            {
                commit,
                render: () => {},
            } as unknown as Partial<DetailStripContext>,
        );

        clips = getClips();

        const toggle = checkbox(body, "Clip Length from Audio");
        if (toggle) {
            toggle.checked = true;
            toggle.dispatchEvent(new Event("change"));
        }

        const clip = clips[0];
        expect(clip.references.map((r) => r.drivesClipLength)).toEqual([
            false,
            true,
        ]);
        expect(clip.duration).toBe(4.5);
        expect(clip.clipLengthFromAudio).toBe(false);
    });

    it("adds through the domain operation rather than mutating in place", () => {
        const addClipReference = jest.fn();

        buildBody([], 0, catalogSupportingClipReferences(), {
            addClipReference,
        } as unknown as Partial<DetailStripContext>)
            .querySelector<HTMLButtonElement>(".vst-detail-add-clip-ref")
            ?.click();

        expect(addClipReference).toHaveBeenCalledWith(0);
    });
});
