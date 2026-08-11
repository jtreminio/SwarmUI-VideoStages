import { mediaPreviewSrc } from "../constants";
import { getVideoStagesHostBridge } from "../host";
import { type SourceRange, toInOut } from "../trimGeometry";

export type SidebarMediaKind = "audio" | "video";

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
