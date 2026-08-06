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
import type { ArchitectureModelCatalog } from "../architectures/types";
import { setVideoStagesHostBridgeForTests } from "../host";
import {
    __resetPersistenceForTests,
    getClips,
    getState,
} from "../persistence/repository";
import type { Clip, ClipReference } from "../types";
import { buildClipReferenceSection } from "./clipReferencePanel";
import type { DetailStripContext } from "./context";

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
        __resetPersistenceForTests();
        document.body.innerHTML = "";
        mountPromptBox("");
    });

    afterEach(() => {
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

    it("offers an image source only for image references", () => {
        const labels = (body: HTMLElement) =>
            Array.from(body.querySelectorAll(".vst-detail-field-label")).map(
                (el) => el.textContent,
            );

        expect(labels(buildBody([{ kind: "image" }], 0))).toContain(
            "Image Source",
        );
        expect(labels(buildBody([{ kind: "audio" }], 0))).not.toContain(
            "Image Source",
        );
    });

    it("shows the soundtrack toggle only on a video reference", () => {
        const hasToggle = (body: HTMLElement) =>
            Array.from(body.querySelectorAll("label")).some((el) =>
                el.textContent?.includes("Include soundtrack"),
            );

        expect(hasToggle(buildBody([{ kind: "video" }], 0))).toBe(true);
        expect(hasToggle(buildBody([{ kind: "image" }], 0))).toBe(false);
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
