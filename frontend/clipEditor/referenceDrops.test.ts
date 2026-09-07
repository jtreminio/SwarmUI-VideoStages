import { afterEach, describe, expect, it, jest } from "@jest/globals";
import {
    resetBrowserMediaStoreForTests,
    resolveBrowserMedia,
} from "../browserMediaStore";
import type { DetailSelectionDomainOperations } from "../detailStrip/selectionDomainOperations";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import { bindReferenceDrop } from "./referenceDrops";

const transferFiles = (files: File[]): DataTransfer =>
    ({
        files,
        items: [],
        types: ["Files"],
        dropEffect: "none",
    }) as unknown as DataTransfer;

const dispatchDrag = (
    button: HTMLButtonElement,
    type: string,
    transfer: DataTransfer,
): Event => {
    const event = new Event(type, { bubbles: true, cancelable: true });
    Object.defineProperty(event, "dataTransfer", { value: transfer });
    button.dispatchEvent(event);
    return event;
};

const waitFor = async (complete: () => boolean): Promise<void> => {
    for (let attempt = 0; attempt < 100 && !complete(); attempt++) {
        await new Promise((resolve) => setTimeout(resolve, 10));
    }
};

describe("reference file drops", () => {
    afterEach(() => {
        resetBrowserMediaStoreForTests();
        setVideoStagesHostBridgeForTests(null);
    });

    it("offers copy feedback and adds ordered keyframes with partial-add feedback", async () => {
        const showError = jest.fn();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            showError,
        });
        const addRefEntries = jest.fn<
            DetailSelectionDomainOperations["addRefEntries"]
        >(() => 1);
        const button = document.createElement("button");
        bindReferenceDrop(button, "clip-a", "keyframe", {
            addRefEntries,
            addClipReferences: jest.fn(() => 0),
        });
        const transfer = transferFiles([
            new File(["first"], "first.png", { type: "image/png" }),
            new File(["second"], "second.png", { type: "image/png" }),
        ]);
        const over = dispatchDrag(button, "dragover", transfer);
        expect(over.defaultPrevented).toBe(true);
        expect(transfer.dropEffect).toBe("copy");
        expect(button.classList).toContain("vst-file-drop-hover");

        const drop = dispatchDrag(button, "drop", transfer);
        await waitFor(() => addRefEntries.mock.calls.length > 0);

        expect(drop.defaultPrevented).toBe(true);
        expect(addRefEntries).toHaveBeenCalledTimes(1);
        expect(addRefEntries).toHaveBeenCalledWith("clip-a", [
            { fileName: "first.png", data: expect.any(String) },
            { fileName: "second.png", data: expect.any(String) },
        ]);
        expect(
            addRefEntries.mock.calls[0][1].map(({ data }) =>
                resolveBrowserMedia(data),
            ),
        ).toEqual([
            "data:image/png;base64,Zmlyc3Q=",
            "data:image/png;base64,c2Vjb25k",
        ]);
        expect(showError).toHaveBeenCalledWith(
            "Added 1 of 2 keyframes. 1 file could not be read or no supported keyframe position remained.",
        );
    });

    it("adds supported reference media and reports the names of unreadable files", async () => {
        const showError = jest.fn();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            showError,
        });
        const addClipReferences = jest.fn<
            DetailSelectionDomainOperations["addClipReferences"]
        >(() => 1);
        const button = document.createElement("button");
        bindReferenceDrop(button, "clip-b", "clip-reference", {
            addRefEntries: jest.fn(() => 0),
            addClipReferences,
        });

        dispatchDrag(
            button,
            "drop",
            transferFiles([
                new File(["image"], "reference.webp", { type: "image/webp" }),
                new File(["document"], "notes.txt", { type: "text/plain" }),
            ]),
        );
        await waitFor(() => addClipReferences.mock.calls.length > 0);

        expect(addClipReferences).toHaveBeenCalledTimes(1);
        expect(addClipReferences).toHaveBeenCalledWith("clip-b", [
            {
                kind: "image",
                mediaDurationSeconds: 0,
                uploadedMedia: {
                    fileName: "reference.webp",
                    data: expect.any(String),
                },
            },
        ]);
        expect(
            resolveBrowserMedia(
                addClipReferences.mock.calls[0][1][0].uploadedMedia?.data ?? "",
            ),
        ).toBe("data:image/webp;base64,aW1hZ2U=");
        expect(showError).toHaveBeenCalledWith(
            "Unsupported reference files: notes.txt",
        );
    });
});
