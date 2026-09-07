import { describe, expect, it, jest } from "@jest/globals";
import { initVideoFixture } from "../__test_helpers__/clipFixtures";
import { editorHarness } from "../__test_helpers__/editor";
import { MEDIA_SOURCE_PREVIOUS_CLIP } from "../generatedMediaSource";
import { getState } from "../persistence/repository";
import { setSelection } from "../selection";

const required = <T>(value: T | null | undefined): T => {
    if (value == null) throw new Error("Required editor element missing");
    return value;
};

describe("editor lifecycle and executable joins", () => {
    const harness = editorHarness();

    it.each([
        "selection",
        "connector",
    ])("edits the executable join across an empty clip through %s", (entry) => {
        const root = harness.setup([
            { duration: 4, stages: [{}], boundaryOut: "crossfade" },
            { duration: 4, stages: [] },
            { duration: 4, stages: [{}] },
        ]);
        if (entry === "selection")
            setSelection({ kind: "boundary", leftClipIdx: 0 });
        else
            required(
                root.querySelector<HTMLButtonElement>(
                    `[data-testid="edit-join"][data-left-clip-id="${getState().clips[0].id}"]`,
                ),
            ).click();
        expect(
            root.querySelector<HTMLElement>(".vst-editor-clip.selected")
                ?.dataset.clipIndex,
        ).toBe("2");
        const join = harness.field<HTMLSelectElement>("join", "Join", "select");
        expect(join.value).toBe("crossfade");
        join.value = "continue";
        join.dispatchEvent(new Event("change", { bubbles: true }));
        expect(getState().clips[0].boundaryOut).toBe("continue");
        expect(
            root.querySelector('[data-section="join"]')?.textContent,
        ).toContain("Clip 2");
    });

    it("applies a source-video relationship to the adjacent authored clip", () => {
        const root = harness.setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        required(
            root.querySelector<HTMLButtonElement>('[data-testid="edit-join"]'),
        ).click();
        const join = harness.field<HTMLSelectElement>("join", "Join", "select");
        join.value = "source-video";
        join.dispatchEvent(new Event("change", { bubbles: true }));
        expect(getState().clips[1].initVideo).toMatchObject({
            source: MEDIA_SOURCE_PREVIOUS_CLIP,
            durationSeconds: 4,
        });
        expect(
            root.querySelector<HTMLElement>('[data-clip-index="0"]')?.dataset
                .sourceUsage,
        ).toBe("video");
    });

    it("releases an open preview player on disposal and can open a fresh preview", () => {
        const root = harness.setup([
            {
                duration: 4,
                stages: [{}],
                initVideo: initVideoFixture({
                    data: "data:video/mp4;base64,AAAA",
                    fileName: "harbor.mp4",
                    durationSeconds: 4,
                }),
            },
        ]);
        required(
            root.querySelector<HTMLButtonElement>(".vst-media-thumbnail"),
        ).click();
        const player = required(
            document.querySelector<HTMLVideoElement>(
                ".vst-media-preview-modal video",
            ),
        );
        const pause = jest.spyOn(player, "pause");
        const reload = jest.spyOn(player, "load");
        harness.editor.dispose();
        expect(pause).toHaveBeenCalledTimes(1);
        expect(reload).toHaveBeenCalledTimes(1);
        harness.editor.init();
        required(
            root.querySelector<HTMLButtonElement>(".vst-media-thumbnail"),
        ).click();
        expect(
            document.querySelectorAll(".vst-media-preview-modal"),
        ).toHaveLength(1);
        expect(
            document.querySelector<HTMLVideoElement>(
                ".vst-media-preview-modal video",
            )?.src,
        ).toBe("data:video/mp4;base64,AAAA");
    });
});
