import { beforeEach, describe, expect, it } from "@jest/globals";
import {
    EDITOR_TAB_ID,
    mountClipEditor,
    updateEditorTabIndicator,
} from "./tab";

const link = (): HTMLAnchorElement => {
    const node = document.querySelector<HTMLAnchorElement>(
        `a[href="#${EDITOR_TAB_ID}"]`,
    );
    if (!node) throw new Error("Production tab link missing");
    return node;
};

describe("VideoStages tab indicator", () => {
    beforeEach(() => {
        document.body.innerHTML =
            '<ul id="bottombartabcollection"></ul><div id="t2i_bottom_bar_content"></div>';
        mountClipEditor();
    });
    it("identifies the enabled production tab with an accessible label", () => {
        updateEditorTabIndicator(true);
        expect(link().title).toBe("Video Stages is enabled");
        expect(link().querySelector(".vst-tab-check")?.textContent).toBe("✓");
        expect(
            link().querySelector(".vst-tab-check")?.getAttribute("aria-hidden"),
        ).toBe("true");
    });
    it("keeps one status mark across repeated enabled refreshes", () => {
        updateEditorTabIndicator(true);
        updateEditorTabIndicator(true);
        updateEditorTabIndicator(true);
        expect(link().querySelectorAll(".vst-tab-check")).toHaveLength(1);
    });
    it("restores the regular production label when disabled", () => {
        updateEditorTabIndicator(true);
        updateEditorTabIndicator(false);
        expect(link().textContent).toBe("VideoStages");
        expect(link().title).toBe("");
    });
});
