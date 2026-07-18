import { beforeEach, describe, expect, it } from "@jest/globals";
import { updateTimelineTabIndicator } from "./bottomTimelineTab";

const TAB_HREF = "#VideoStages-Timeline-Tab";

const mountNav = (): HTMLAnchorElement => {
    document.body.innerHTML =
        `<ul id="bottombartabcollection">` +
        `<li class="nav-item"><a class="nav-link" href="${TAB_HREF}">Timeline</a></li>` +
        `</ul>`;
    return document.querySelector(`a[href="${TAB_HREF}"]`) as HTMLAnchorElement;
};

describe("updateTimelineTabIndicator", () => {
    beforeEach(() => {
        document.body.innerHTML = "";
    });

    it("adds a green check to the Timeline tab when enabled", () => {
        const navLink = mountNav();
        updateTimelineTabIndicator(true);
        const mark = navLink.querySelector(".vst-tab-check");
        expect(mark).not.toBeNull();
        expect(mark?.textContent).toBe("✓");
        expect(mark?.getAttribute("aria-hidden")).toBe("true");
        expect(navLink.title).toBe("Video Stages is enabled");
    });

    it("is idempotent — repeated enabled calls keep a single check", () => {
        const navLink = mountNav();
        updateTimelineTabIndicator(true);
        updateTimelineTabIndicator(true);
        expect(navLink.querySelectorAll(".vst-tab-check")).toHaveLength(1);
    });

    it("removes the check when disabled", () => {
        const navLink = mountNav();
        updateTimelineTabIndicator(true);
        updateTimelineTabIndicator(false);
        expect(navLink.querySelector(".vst-tab-check")).toBeNull();
        expect(navLink.hasAttribute("title")).toBe(false);
    });

    it("keeps the tab label text intact around toggles", () => {
        const navLink = mountNav();
        updateTimelineTabIndicator(true);
        updateTimelineTabIndicator(false);
        expect(navLink.textContent).toBe("Timeline");
    });

    it("is a no-op when the tab nav link is absent", () => {
        expect(() => updateTimelineTabIndicator(true)).not.toThrow();
    });
});
