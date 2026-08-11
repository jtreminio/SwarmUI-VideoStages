import { mediaPreviewSrc } from "../constants";
import { getVideoStagesHostBridge } from "../host";
import { type SourceRange, toInOut } from "../trimGeometry";

export const buildSidebarVideoPreview = (
    dataUri: string,
    range: SourceRange,
): HTMLElement => {
    const section = document.createElement("div");
    section.className = "vst-sidebar-video-preview-section";
    const label = document.createElement("span");
    label.className = "vst-sidebar-video-preview-label";
    label.textContent = "Preview";
    const player = getVideoStagesHostBridge().createInitVideoElement();
    player.className = "vst-sidebar-video-preview";
    player.controls = true;
    player.preload = "metadata";
    player.setAttribute("playsinline", "");
    player.src = mediaPreviewSrc(dataUri);
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
    player.addEventListener("seeking", keepSeekInRange);
    player.addEventListener("timeupdate", stopAtOut);
    player.addEventListener("ended", stopAtOut);
    section.append(label, player);
    return section;
};

export const releaseSidebarVideoPreviews = (root: ParentNode): void => {
    for (const player of root.querySelectorAll<HTMLVideoElement>(
        ".vst-sidebar-video-preview",
    )) {
        player.pause();
        player.removeAttribute("src");
        player.load();
    }
};
