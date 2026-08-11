import { afterEach, describe, expect, it, jest } from "@jest/globals";

import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import { closeTrimModal, openTrimModal } from "./trimModal";

const DATA = "data:video/mp4;base64,AAAA";
const LIMITS = { limitSeconds: 12.4, minLengthSeconds: 1, fps: 0 };

const field = (name: "in" | "out" | "duration"): HTMLInputElement => {
    const input = document.querySelector<HTMLInputElement>(
        `[data-vst-trim-field="${name}"]`,
    );
    if (!input) {
        throw new Error(`${name} field missing`);
    }
    return input;
};

const open = (
    onApply = jest.fn(),
    createPlayer?: () => HTMLVideoElement,
): jest.Mock => {
    setVideoStagesHostBridgeForTests({
        ...createDefaultVideoStagesHostBridge(),
        createInitVideoElement:
            createPlayer ??
            (() => {
                const video = document.createElement("video");
                video.pause = jest.fn();
                video.load = jest.fn();
                video.play = jest.fn(() => Promise.resolve());
                return video;
            }),
    });
    openTrimModal({
        mediaKind: "video",
        title: "Trim Source Video",
        fileName: "beach.mp4",
        dataUri: DATA,
        range: { startSeconds: 2, lengthSeconds: 4 },
        limits: LIMITS,
        impactText: (range) =>
            `Clip duration becomes ${range.lengthSeconds.toFixed(1)} s`,
        onApply,
    });
    return onApply;
};

describe("trim modal", () => {
    afterEach(() => {
        closeTrimModal();
        setVideoStagesHostBridgeForTests(null);
        document.body.innerHTML = "";
    });

    it("opens one large player with a range bar and exact fields", () => {
        open();

        const modal = document.querySelector<HTMLElement>(".vst-trim-modal");
        expect(modal?.getAttribute("role")).toBe("dialog");
        expect(modal?.textContent).toContain("Trim Source Video");
        expect(modal?.textContent).toContain("beach.mp4");
        expect(modal?.querySelectorAll("video")).toHaveLength(1);
        expect(modal?.querySelector(".vst-trim")).not.toBeNull();
        expect(field("in").value).toBe("2.0");
        expect(field("out").value).toBe("6.0");
        expect(field("duration").value).toBe("4.0");
    });

    it("keeps edits in the modal until Apply", () => {
        const onApply = open();
        field("in").value = "3";
        field("in").dispatchEvent(new Event("input", { bubbles: true }));

        expect(field("out").value).toBe("6.0");
        expect(field("duration").value).toBe("3.0");
        expect(document.body.textContent).toContain(
            "Clip duration becomes 3.0 s",
        );
        expect(onApply).not.toHaveBeenCalled();

        document
            .querySelector<HTMLButtonElement>("[data-vst-trim-apply]")
            ?.click();

        expect(onApply).toHaveBeenCalledWith({
            startSeconds: 3,
            lengthSeconds: 3,
        });
        expect(document.querySelector(".vst-trim-modal")).toBeNull();
    });

    it("resets to the full source and cancels without applying", () => {
        const onApply = open();
        document
            .querySelector<HTMLButtonElement>("[data-vst-trim-reset]")
            ?.click();

        expect(field("in").value).toBe("0.0");
        expect(field("out").value).toBe("12.4");
        expect(field("duration").value).toBe("12.4");

        document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));
        expect(onApply).not.toHaveBeenCalled();
        expect(document.querySelector(".vst-trim-modal")).toBeNull();
    });

    it("marks In and Out at the player's current frame", () => {
        open();
        const video = document.querySelector<HTMLVideoElement>(
            ".vst-trim-modal-player",
        );
        if (!video) {
            throw new Error("trim player missing");
        }

        video.currentTime = 3.2;
        document.dispatchEvent(new KeyboardEvent("keydown", { key: "i" }));
        expect(field("in").value).toBe("3.2");
        expect(field("out").value).toBe("6.0");

        video.currentTime = 5.4;
        document.dispatchEvent(new KeyboardEvent("keydown", { key: "o" }));
        expect(field("out").value).toBe("5.4");
        expect(field("duration").value).toBe("2.2");
    });

    it("slides the range at the same length when Mark In is past Out", () => {
        open();
        const video = document.querySelector<HTMLVideoElement>(
            ".vst-trim-modal-player",
        );
        const markIn = Array.from(
            document.querySelectorAll<HTMLButtonElement>(
                ".vst-trim-modal-transport button",
            ),
        ).find((candidate) => candidate.textContent === "Mark In");
        if (!video || !markIn) {
            throw new Error("Mark In controls missing");
        }

        video.currentTime = 7;
        markIn.click();

        expect(field("in").value).toBe("7.0");
        expect(field("out").value).toBe("11.0");
        expect(field("duration").value).toBe("4.0");
    });

    it("scrubs the player to the edge being dragged", () => {
        open();
        const video = document.querySelector<HTMLVideoElement>(
            ".vst-trim-modal-player",
        );
        const track = document.querySelector<HTMLElement>(".vst-trim-track");
        const inGrip = document.querySelector<HTMLElement>(
            '[data-vst-trim-grip="in"]',
        );
        const outGrip = document.querySelector<HTMLElement>(
            '[data-vst-trim-grip="out"]',
        );
        if (!video || !track || !inGrip || !outGrip) {
            throw new Error("trim drag controls missing");
        }
        track.getBoundingClientRect = jest.fn(
            () => ({ left: 0, width: 100 }) as DOMRect,
        );
        const pointer = (type: string, clientX: number): Event => {
            const event = new Event(type, { bubbles: true });
            Object.defineProperties(event, {
                button: { value: 0 },
                clientX: { value: clientX },
                pointerId: { value: 1 },
            });
            return event;
        };

        inGrip.dispatchEvent(pointer("pointerdown", 16));
        inGrip.dispatchEvent(pointer("pointermove", 24));
        expect(video.currentTime).toBe(3);
        inGrip.dispatchEvent(pointer("pointerup", 24));

        outGrip.dispatchEvent(pointer("pointerdown", 48));
        outGrip.dispatchEvent(pointer("pointermove", 80));
        expect(video.currentTime).toBe(9.9);
        outGrip.dispatchEvent(pointer("pointerup", 80));
    });

    it("previews only the selected range and returns to In", () => {
        open();
        const video = document.querySelector<HTMLVideoElement>(
            ".vst-trim-modal-player",
        );
        const preview = Array.from(
            document.querySelectorAll<HTMLButtonElement>(
                ".vst-trim-modal-transport button",
            ),
        ).find((candidate) => candidate.textContent?.includes("Preview range"));
        if (!video || !preview) {
            throw new Error("preview controls missing");
        }

        preview.click();
        expect(video.currentTime).toBe(2);
        expect(video.play).toHaveBeenCalledTimes(1);

        video.currentTime = 6;
        video.dispatchEvent(new Event("timeupdate"));
        expect(video.pause).toHaveBeenCalledTimes(1);
        expect(video.currentTime).toBe(2);
    });

    it("uses the host player and releases its source on close", () => {
        const video = document.createElement("video");
        video.pause = jest.fn();
        video.load = jest.fn();
        video.play = jest.fn(() => Promise.resolve());
        open(jest.fn(), () => video);

        expect(
            document.querySelector<HTMLVideoElement>(".vst-trim-modal-player"),
        ).toBe(video);
        closeTrimModal();

        expect(video.pause).toHaveBeenCalledTimes(1);
        expect(video.load).toHaveBeenCalledTimes(1);
        expect(video.hasAttribute("src")).toBe(false);
    });

    it("uses an audio player for an audio source", () => {
        const fallbackVideo = document.createElement("video");
        fallbackVideo.pause = jest.fn();
        fallbackVideo.load = jest.fn();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            createInitVideoElement: () => fallbackVideo,
        });
        openTrimModal({
            mediaKind: "audio",
            title: "Trim Audio Track",
            fileName: "score.wav",
            dataUri: "data:audio/wav;base64,AAAA",
            range: { startSeconds: 1, lengthSeconds: 3 },
            limits: LIMITS,
            impactText: (range) =>
                `Track length becomes ${range.lengthSeconds.toFixed(1)} s`,
            onApply: jest.fn(),
        } as Parameters<typeof openTrimModal>[0]);

        const audio = document.querySelector<HTMLAudioElement>(
            ".vst-trim-modal-player",
        );
        if (audio) {
            audio.pause = jest.fn();
            audio.load = jest.fn();
        }
        expect(audio?.tagName).toBe("AUDIO");
        expect(document.body.textContent).toContain("Use full audio");
    });
});
