import { mediaPreviewSrc } from "../constants";
import { getVideoStagesHostBridge } from "../host";
import { type SourceRange, toInOut } from "../trimGeometry";
import { createManagedModal } from "./modalManager";

export type SidebarMediaKind = "audio" | "video";

export interface MediaPreviewPlayback {
    kind: SidebarMediaKind;
    range: SourceRange;
}

export const makeMediaPreviewExpandable = (
    preview: HTMLElement,
    src: string,
    name: string,
    playback?: MediaPreviewPlayback,
): void => {
    preview.querySelector(".auto-input-remove-button")?.remove();
    const media = preview.querySelector("img, video, audio");
    if (!media) return;
    const kind = playback?.kind ?? media.tagName.toLowerCase();
    const button = document.createElement("button");
    button.type = "button";
    button.className = "basic-button vst-media-thumbnail";
    button.dataset.testid = "media-preview";
    button.dataset.mediaKind = kind;
    button.setAttribute("aria-label", `Preview ${name}`);
    const hint = document.createElement("span");
    hint.className = "vst-media-thumbnail-hint";
    hint.textContent =
        kind === "audio" ? "▶ Audio" : kind === "video" ? "▶ Video" : "↗";
    hint.setAttribute("aria-hidden", "true");
    media.addEventListener("error", () => {
        hint.textContent = "Preview unavailable";
        button.setAttribute("aria-label", `Preview unavailable: ${name}`);
    });
    if (media instanceof HTMLMediaElement) {
        media.controls = false;
        media.muted = true;
        if (kind === "audio") media.hidden = true;
    }
    button.append(media, hint);
    preview.replaceChildren(button);
    button.addEventListener("click", () => {
        const modal = createManagedModal({
            modalClass: "vst-media-preview-modal",
            backdropClass: "vst-media-preview-backdrop",
            labelledBy: "vst-media-preview-title",
            onClose: () => {
                releaseSidebarMediaPreviews(modal.body);
                if (button.isConnected) button.focus();
            },
            onKeyDown: (event) => {
                if (event.key !== "Tab") return;
                const controls = Array.from(
                    modal.modal.querySelectorAll<HTMLElement>(
                        "button, video[controls], audio[controls]",
                    ),
                );
                const first = controls[0];
                const last = controls.at(-1);
                if (event.shiftKey && document.activeElement === first) {
                    event.preventDefault();
                    last?.focus();
                } else if (!event.shiftKey && document.activeElement === last) {
                    event.preventDefault();
                    first?.focus();
                }
            },
        });
        const title = document.createElement("h5");
        title.id = "vst-media-preview-title";
        title.className = "modal-title";
        title.textContent = name;
        const close = document.createElement("button");
        close.type = "button";
        close.className = "basic-button";
        close.textContent = "Close";
        close.addEventListener("click", modal.close);
        modal.header.append(title, close);
        if (kind === "audio" || kind === "video") {
            modal.body.append(
                buildSidebarMediaPreview(
                    kind,
                    src,
                    playback?.range ?? { startSeconds: 0, lengthSeconds: 0 },
                ),
            );
        } else {
            const image = document.createElement("img");
            image.alt = name;
            image.src = mediaPreviewSrc(src);
            modal.body.append(image);
        }
        modal.open(close);
    });
};

export const buildSidebarMediaPreview = (
    mediaKind: SidebarMediaKind,
    dataUri: string,
    range: SourceRange,
): HTMLElement => {
    const section = document.createElement("div");
    section.className = "vst-sidebar-media-preview-section";
    const label = document.createElement("span");
    label.className = "vst-sidebar-media-preview-label";
    label.textContent = "Preview";
    const player: HTMLMediaElement =
        mediaKind === "video"
            ? getVideoStagesHostBridge().createInitVideoElement()
            : document.createElement("audio");
    player.className = `vst-sidebar-media-preview vst-sidebar-${mediaKind}-preview`;
    player.controls = true;
    player.preload = "metadata";
    player.setAttribute("playsinline", "");
    const mediaError = document.createElement("div");
    mediaError.className = "vst-sidebar-media-preview-error";
    mediaError.textContent = `Cannot preview this ${mediaKind}.`;
    mediaError.hidden = true;
    const bounds = () => (range.lengthSeconds > 0 ? toInOut(range) : null);
    const resetToIn = (): void => {
        const selected = bounds();
        if (selected && player.currentTime !== selected.inSeconds) {
            player.currentTime = selected.inSeconds;
        }
    };
    const keepSeekInRange = (): void => {
        const selected = bounds();
        if (
            selected &&
            (player.currentTime < selected.inSeconds ||
                player.currentTime >= selected.outSeconds)
        ) {
            resetToIn();
        }
    };
    const stopAtOut = (): void => {
        const selected = bounds();
        if (!selected) {
            return;
        }
        if (player.currentTime >= selected.outSeconds) {
            player.pause();
            resetToIn();
        } else if (player.currentTime < selected.inSeconds) {
            resetToIn();
        }
    };
    player.addEventListener("loadedmetadata", resetToIn);
    player.addEventListener("play", keepSeekInRange);
    player.addEventListener(
        mediaKind === "audio" ? "seeked" : "seeking",
        keepSeekInRange,
    );
    player.addEventListener("timeupdate", stopAtOut);
    player.addEventListener("ended", stopAtOut);
    player.addEventListener("error", () => {
        player.hidden = true;
        mediaError.hidden = false;
    });
    player.src = mediaPreviewSrc(dataUri);
    section.append(label, player, mediaError);
    return section;
};

export const releaseSidebarMediaPreviews = (root: ParentNode): void => {
    for (const player of root.querySelectorAll<HTMLMediaElement>(
        ".vst-sidebar-media-preview",
    )) {
        player.pause();
        player.removeAttribute("src");
        player.load();
    }
};
