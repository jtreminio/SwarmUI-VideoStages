import type { ArchitectureCatalogSnapshot } from "./architectures/types";

const detailDock = (body: HTMLElement): HTMLElement | null =>
    body.parentElement?.querySelector<HTMLElement>(":scope > .vst-detail") ??
    null;

const setDockAvailable = (body: HTMLElement, available: boolean): void => {
    const dock = detailDock(body);
    if (dock) {
        dock.hidden = !available;
    }
};

const retryButton = (onRetry: () => void): HTMLButtonElement => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "basic-button vst-catalog-retry";
    button.textContent = "Retry";
    button.addEventListener("click", onRetry);
    return button;
};

/**
 * Renders the blocking initial states. Returns true when normal timeline
 * rendering must stop before reading or normalizing the authoring document.
 */
export const renderBlockingArchitectureCatalogStatus = (
    body: HTMLElement,
    snapshot: ArchitectureCatalogSnapshot,
    onRetry: () => void,
): boolean => {
    if (snapshot.catalog) {
        setDockAvailable(body, true);
        return false;
    }

    setDockAvailable(body, false);
    body.replaceChildren();
    const status = document.createElement("section");
    status.className = "vst-catalog-status";
    status.setAttribute("role", "status");
    status.dataset.catalogStatus = snapshot.status;

    const title = document.createElement("strong");
    title.className = "vst-catalog-status-title";
    if (snapshot.status === "loading") {
        title.textContent = "Loading video model capabilities…";
        status.setAttribute("aria-busy", "true");
        status.appendChild(title);
        body.appendChild(status);
        return true;
    }
    title.textContent = "Video model capabilities are unavailable";
    const explanation = document.createElement("p");
    explanation.textContent =
        "VideoStages cannot safely author clips until the backend catalog is available.";
    status.append(title, explanation, retryButton(onRetry));
    body.appendChild(status);
    return true;
};

/** Adds a nonblocking notice while a last-known-good catalog remains active. */
export const renderRetainedArchitectureCatalogStatus = (
    body: HTMLElement,
    snapshot: ArchitectureCatalogSnapshot,
    onRetry: () => void,
): void => {
    body.querySelector(":scope > .vst-catalog-notice")?.remove();
    if (snapshot.status !== "refreshing" && snapshot.status !== "stale") {
        return;
    }
    const notice = document.createElement("div");
    notice.className = `vst-catalog-notice vst-catalog-notice-${snapshot.status}`;
    notice.dataset.catalogStatus = snapshot.status;
    notice.setAttribute(
        "role",
        snapshot.status === "stale" ? "alert" : "status",
    );
    const text = document.createElement("span");
    text.textContent =
        snapshot.status === "refreshing"
            ? "Refreshing video model capabilities…"
            : "Using the last known video model capabilities; refresh failed.";
    notice.appendChild(text);
    if (snapshot.status === "stale") {
        notice.appendChild(retryButton(onRetry));
    }
    body.prepend(notice);
};
