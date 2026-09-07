import { expect, test } from "@playwright/test";

test("edits a video through the continuous clip editor at laptop size", async ({
    page,
}) => {
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(error.message));
    await page.setViewportSize({ width: 1512, height: 982 });
    await page.goto("/Text2Image", { waitUntil: "domcontentloaded" });
    const tab = page.getByTestId("clip-editor-tab");
    await expect(tab).toBeVisible({ timeout: 30_000 });
    await tab.click();
    const editor = page.getByTestId("clip-editor");
    await expect(editor).toBeVisible();
    await page.getByTestId("add-first-clip").click();
    const prompt = editor.getByTestId("section-prompt").locator("textarea");
    await expect(prompt).toBeFocused();
    await prompt.fill("A quiet street under evening rain");
    await editor.getByTestId("nav-stages").click();
    await expect(prompt).toHaveCount(1);
    const sections = editor.getByTestId("clip-sections");
    await expect
        .poll(() => sections.evaluate((node) => node.scrollTop))
        .toBeGreaterThan(0);
    await expect
        .poll(() => sections.evaluate((node) => node.clientHeight))
        .toBeLessThan(500);
    await expect(editor.getByTestId("nav-stages")).toHaveAttribute(
        "aria-current",
        "location",
    );
    await editor
        .getByTestId("section-stages")
        .getByTestId("field-steps-number")
        .fill("12");
    await editor.getByTestId("nav-prompt").click();
    await expect(prompt).toHaveValue("A quiet street under evening rain");
    await editor.getByTestId("clip-stages").click();
    await expect(
        editor.getByTestId("section-stages").getByTestId("field-steps-number"),
    ).toHaveValue("12");
    await editor.getByTestId("nav-keyframes").click();
    await editor.getByTestId("add-keyframes").click();
    await expect(
        editor.getByTestId("section-keyframes").getByTestId("editor-item"),
    ).toHaveCount(1);
    await editor.getByTestId("add-clip").click();
    await expect(editor.getByTestId("clip-card")).toHaveCount(2);
    await expect(prompt).toBeFocused();
    await prompt.fill("The camera turns toward the river");
    await editor.getByTestId("edit-join").click();
    await expect(editor.getByTestId("nav-join")).toHaveAttribute(
        "aria-current",
        "location",
    );
    await editor
        .getByTestId("section-join")
        .getByTestId("field-join-select")
        .selectOption("crossfade");
    await editor
        .getByTestId("clip-card")
        .first()
        .getByTestId("clip-prompt")
        .click();
    await expect(prompt).toBeFocused();
    await expect(prompt).toHaveValue("A quiet street under evening rain");
    await sections.evaluate((node) => {
        const target = node.querySelector<HTMLElement>(
            '[data-section="audio"]',
        );
        if (target)
            node.scrollTop +=
                target.getBoundingClientRect().top -
                node.getBoundingClientRect().top;
    });
    await expect(editor.getByTestId("nav-audio")).toHaveAttribute(
        "aria-current",
        "location",
    );
    await expect(page.locator("#input_prompt")).toHaveValue(
        /A quiet street under evening rain/,
    );
    await expect
        .poll(() =>
            page
                .locator("#input_videostages")
                .inputValue()
                .then(JSON.parse)
                .then((state) => state.clips[0].stages[0].steps),
        )
        .toBe(12);
    await expect
        .poll(() =>
            page
                .locator("#input_videostages")
                .inputValue()
                .then(JSON.parse)
                .then((state) => state.clips[0].boundaryOut),
        )
        .toBe("crossfade");
    await editor
        .getByTestId("clip-card")
        .first()
        .getByTestId("clip-actions")
        .click();
    await page.getByText("Move later", { exact: true }).click();
    await expect(
        editor.getByTestId("clip-card").last().getByTestId("clip-prompt"),
    ).toContainText("A quiet street under evening rain");
    await expect(prompt).toHaveValue("A quiet street under evening rain");
    await editor
        .getByTestId("clip-card")
        .last()
        .getByTestId("clip-actions")
        .click();
    await page.getByText("Move earlier", { exact: true }).click();
    await expect(
        editor.getByTestId("clip-card").first().getByTestId("clip-prompt"),
    ).toContainText("A quiet street under evening rain");
    await page.reload();
    await tab.click();
    await expect(editor.getByTestId("clip-card")).toHaveCount(2);
    await editor
        .getByTestId("clip-card")
        .first()
        .getByTestId("clip-prompt")
        .click();
    await expect(prompt).toHaveValue("A quiet street under evening rain");
    await expect(
        editor.getByTestId("section-stages").getByTestId("field-steps-number"),
    ).toHaveValue("12");
    expect(pageErrors).toEqual([]);
});

test("keeps strip controls reachable and lays Bonita out horizontally", async ({
    page,
}) => {
    await page.setViewportSize({ width: 1512, height: 982 });
    await page.goto("/Text2Image", { waitUntil: "domcontentloaded" });
    await page.getByTestId("clip-editor-tab").click();
    const editor = page.getByTestId("clip-editor");
    await editor.getByTestId("add-first-clip").click();
    await editor.getByTestId("add-stages").click();
    const pane = editor.getByTestId("clip-sections");
    const stage = editor
        .getByTestId("section-stages")
        .getByTestId("editor-item")
        .last();
    await expect
        .poll(async () => {
            const item = await stage.boundingBox();
            const area = await pane.boundingBox();
            return Boolean(
                item &&
                    area &&
                    item.y >= area.y + 40 &&
                    item.y < area.y + area.height,
            );
        })
        .toBe(true);
    const bonita = stage.locator(".vst-detail-section").filter({
        hasText: "Make Them Bonita",
    });
    const header = bonita.locator(":scope > .vst-detail-section-header");
    await header.press("Enter");
    const fields = stage.locator(".vst-character-refine-fields");
    await expect(fields).toBeVisible();
    await expect(
        fields.getByRole("combobox", { name: "? Finder", exact: true }),
    ).toBeDisabled();
    await bonita
        .getByRole("checkbox", {
            name: "Turn on Make Them Bonita",
            exact: true,
        })
        .click();
    await expect(header).toHaveAttribute("aria-expanded", "true");
    await expect(
        fields.getByRole("combobox", { name: "? Finder", exact: true }),
    ).toBeEnabled();
    await expect
        .poll(async () => {
            const boxes = await fields
                .locator(":scope > .auto-input")
                .evaluateAll((nodes) =>
                    nodes
                        .slice(0, 3)
                        .map((node) => node.getBoundingClientRect().toJSON()),
                );
            return (
                boxes.length === 3 &&
                Math.abs(boxes[0].y - boxes[1].y) < 2 &&
                Math.abs(boxes[1].y - boxes[2].y) < 2 &&
                boxes[0].x < boxes[1].x &&
                boxes[1].x < boxes[2].x
            );
        })
        .toBe(true);
    for (let index = 0; index < 5; index++)
        await editor.getByTestId("add-clip").click();
    await expect(editor.getByTestId("clip-card")).toHaveCount(6);
    const sequence = editor.getByTestId("clip-sequence");
    const toggle = editor.getByTestId("toggle-enabled");
    const switchBefore = await toggle.boundingBox();
    await sequence.evaluate((node) => {
        node.scrollTop = node.scrollHeight;
    });
    await expect
        .poll(() => sequence.evaluate((node) => node.scrollTop))
        .toBeGreaterThan(0);
    const switchAfter = await toggle.boundingBox();
    expect(switchAfter?.x).toBe(switchBefore?.x);
    await toggle.uncheck();
    await expect(editor.locator(".vst-editor-status strong")).toHaveText(
        "Disabled",
    );
    await expect(editor.getByTestId("clip-strip")).toHaveAttribute(
        "data-enabled",
        "false",
    );
    expect(
        await editor
            .getByTestId("clip-strip")
            .evaluate((node) => getComputedStyle(node, "::after").maskImage),
    ).toContain("VIDEOSTAGES");
    await editor
        .getByTestId("clip-card")
        .first()
        .click({ position: { x: 2, y: 30 } });
    await expect(editor.locator(".vst-editor-clip.selected")).toHaveAttribute(
        "data-clip-index",
        "0",
    );
    await expect(
        editor.getByTestId("section-prompt").locator("textarea"),
    ).toBeEnabled();
});

for (const width of [1512, 1920]) {
    test(`uses consistent field geometry across sections at ${width}px`, async ({
        page,
    }) => {
        await page.setViewportSize({ width, height: 982 });
        await page.goto("/Text2Image", { waitUntil: "domcontentloaded" });
        await page.getByTestId("clip-editor-tab").click();
        await page.getByTestId("add-first-clip").click();
        const sizes = () =>
            page.getByTestId("clip-editor").evaluate((root) => {
                const duration = root.querySelector(
                    '[data-section="prompt"] .vst-stage-slider',
                );
                const steps = root.querySelector(
                    '[data-section="stages"] .vst-stage-slider',
                );
                const resize = root.querySelector(
                    '[data-testid="field-reference-resize-select"]',
                );
                const model = root.querySelector(
                    '[data-section="stages"] select',
                );
                if (!duration || !steps || !resize || !model)
                    throw Error("Missing comparison fields");
                return [duration, steps, resize, model].map(
                    (el) => el.getBoundingClientRect().width,
                );
            });
        const before = await sizes();
        expect(before[3]).toBeGreaterThan(before[1]);
        for (const field of before) expect(field).toBeGreaterThan(100);
        await page.getByTestId("nav-keyframes").click();
        await page.getByTestId("add-keyframes").click();
        const after = await sizes();
        for (let i = 0; i < before.length; i++)
            expect(Math.abs(before[i] - after[i])).toBeLessThan(2);
        await page.getByTestId("nav-prompt").click();
        await page.getByTestId("field-audio-only-checkbox").check();
        await expect(page.getByTestId("clip-card")).toHaveAttribute(
            "data-media-kind",
            "audio",
        );
        await expect(page.getByTestId("clip-card")).toHaveAttribute(
            "data-source-usage",
            "",
        );
        await expect(page.getByTestId("clip-card")).toHaveCSS(
            "box-shadow",
            /inset/,
        );
    });
}

test("authors dropped references and a retake in a narrow production editor", async ({
    page,
}) => {
    await page.setViewportSize({ width: 900, height: 982 });
    await page.goto("/Text2Image", { waitUntil: "domcontentloaded" });
    await page.getByTestId("clip-editor-tab").click();
    const editor = page.getByTestId("clip-editor");
    await editor.getByTestId("add-first-clip").click();
    const transfer = await page.evaluateHandle(() => {
        const bytes = Uint8Array.from(
            atob(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=",
            ),
            (value) => value.charCodeAt(0),
        );
        const data = new DataTransfer();
        data.items.add(
            new File([bytes], "browser-reference.png", { type: "image/png" }),
        );
        return data;
    });
    await editor.getByTestId("nav-keyframes").click();
    await editor
        .getByTestId("add-keyframes")
        .dispatchEvent("drop", { dataTransfer: transfer });
    const keyframes = editor.getByTestId("section-keyframes");
    await expect(keyframes.locator(".vst-media-thumbnail")).toBeVisible();
    await keyframes.locator(".vst-media-thumbnail").click();
    await expect(page.locator(".vst-media-preview-modal img")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(keyframes.locator(".vst-media-thumbnail")).toBeFocused();
    await editor.getByTestId("nav-references").click();
    await editor
        .getByTestId("add-references")
        .dispatchEvent("drop", { dataTransfer: transfer });
    await expect(
        editor
            .getByTestId("section-references")
            .locator(".vst-media-thumbnail"),
    ).toBeVisible();
    await transfer.dispose();
    await editor.getByTestId("add-clip").click();
    await editor.getByTestId("nav-source-video").click();
    await editor
        .getByTestId("section-source-video")
        .locator("select.vst-init-video-source")
        .selectOption({ label: "Previous Clip Output" });
    await editor.getByTestId("nav-retake").click();
    const retake = editor.getByTestId("section-retake");
    await retake
        .getByRole("button", { name: "Add a retake window", exact: true })
        .click();
    const start = retake.locator('[data-vst-focus-key$="retake-start"]');
    await start.fill("1");
    await start.press("Tab");
    await expect
        .poll(async () => {
            const state = JSON.parse(
                await page.locator("#input_videostages").inputValue(),
            );
            return state.clips[1].retake.startSeconds;
        })
        .toBe(1);
    const pane = editor.getByTestId("clip-sections");
    expect(
        await pane.evaluate((node) => node.scrollWidth <= node.clientWidth + 1),
    ).toBe(true);
    await page.reload();
    await page.getByTestId("clip-editor-tab").click();
    await editor
        .getByTestId("clip-card")
        .last()
        .getByTestId("clip-prompt")
        .click();
    await editor.getByTestId("nav-retake").click();
    await expect(start).toHaveValue("1");
});

test("edits the incoming reference supplied by a MiniMax Continue join", async ({
    page,
}) => {
    await page.setViewportSize({ width: 1512, height: 982 });
    await page.goto("/Text2Image", { waitUntil: "domcontentloaded" });
    await page.getByTestId("clip-editor-tab").click();
    const editor = page.getByTestId("clip-editor");
    await editor.getByTestId("add-first-clip").click();
    const model = editor
        .getByTestId("section-stages")
        .getByRole("combobox", { name: "Model", exact: true });
    const referenceModel = await model
        .locator("option")
        .evaluateAll((options) =>
            options
                .map((option) => (option as HTMLOptionElement).value)
                .find((value) => /minimax.*ref2va/i.test(value)),
        );
    expect(
        referenceModel,
        "The backend must expose a MiniMax ref2va model",
    ).toBeTruthy();
    await model.selectOption(referenceModel!);
    await editor.getByTestId("add-clip").click();
    await editor.getByTestId("edit-join").click();
    await editor.getByTestId("field-join-select").selectOption("continue");
    await editor.getByTestId("nav-references").click();
    const references = editor.getByTestId("section-references");
    await expect(
        references.getByRole("heading", {
            name: "<Video 1> · From Clip 0",
            exact: true,
        }),
    ).toBeVisible();
    const scale = references.getByRole("combobox", {
        name: "Reference scale",
        exact: true,
    });
    await scale.selectOption("0.5");
    await references
        .getByRole("checkbox", { name: "Include soundtrack", exact: true })
        .uncheck();
    await expect
        .poll(async () => {
            const source = JSON.parse(
                await page.locator("#input_videostages").inputValue(),
            ).clips[0];
            return [
                source.boundaryOutReferenceScale,
                source.boundaryOutReferenceIncludeSoundtrack,
            ];
        })
        .toEqual([0.5, false]);
    await editor.getByTestId("add-references").click();
    await expect(references.getByTestId("editor-item")).toHaveCount(2);
    await page.reload();
    await page.getByTestId("clip-editor-tab").click();
    await editor
        .getByTestId("clip-card")
        .last()
        .getByTestId("clip-prompt")
        .click();
    await editor.getByTestId("nav-references").click();
    await expect(scale).toHaveValue("0.5");
    await expect(
        references.getByRole("heading", {
            name: "<Video 1> · From Clip 0",
            exact: true,
        }),
    ).toBeVisible();
});

test("opens saved clips from a preview link with the production tab selected", async ({
    page,
}) => {
    await page.goto("/Text2Image", { waitUntil: "domcontentloaded" });
    const tab = page.getByTestId("clip-editor-tab");
    await tab.click();
    await page.getByTestId("add-first-clip").click();
    const prompt = page.getByTestId("section-prompt").locator("textarea");
    await prompt.fill("A harbor after sunrise");
    await page.getByTestId("nav-stages").click();
    await page.goto("/Text2Image#VideoStages-Editor-Tab,Text2Image");
    await expect(page.getByTestId("clip-editor")).toBeVisible();
    await expect(tab).toHaveAttribute("aria-selected", "true");
    await expect(page).toHaveURL(/#VideoStages-Timeline-Tab,Text2Image$/);
    await page.getByTestId("clip-prompt").click();
    await expect(prompt).toHaveValue("A harbor after sunrise");
});
