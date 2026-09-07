import { describe, expect, it, jest } from "@jest/globals";
import { editorHarness } from "../__test_helpers__/editor";
import { getClips, saveClips } from "../persistence/repository";
import { getSelection } from "../selection";
import { renderEditorDiagnostics } from "./diagnostics";

describe("editor authoring diagnostics", () => {
    const harness = editorHarness();

    it("refreshes diagnostics during a value edit while retaining field focus", () => {
        const root = harness.setup([{ duration: 4, stages: [{}] }]);
        const prompt = root.querySelector<HTMLTextAreaElement>("textarea");
        prompt?.focus();
        const clips = getClips();
        clips[0].retake = { startSeconds: 0, lengthSeconds: 1, strength: 1 };
        saveClips(clips, { valueOnly: true });
        expect(
            root.querySelector('[data-vst-diagnostic="retake-source-required"]')
                ?.textContent,
        ).toContain("Error:");
        expect(document.activeElement).toBe(prompt);
    });

    it("shows the advertised retake source error and navigates to its clip", () => {
        const root = harness.setup([
            { duration: 4, stages: [{}] },
            {
                duration: 4,
                stages: [{}],
                retake: { startSeconds: 0, lengthSeconds: 1, strength: 1 },
            },
        ]);
        const issue = root.querySelector<HTMLElement>(
            '[data-vst-diagnostic="retake-source-required"]',
        );
        expect(issue?.textContent).toContain("Error:");
        expect(issue?.textContent).toContain("Clip 1");
        expect(
            root.querySelector(".vst-editor-diagnostics summary")?.textContent,
        ).toContain("1 error");
        issue?.querySelector<HTMLButtonElement>("button")?.click();
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 1,
            stageIdx: 0,
        });
        expect(
            root
                .querySelector('[data-section-link="stages"]')
                ?.getAttribute("aria-current"),
        ).toBe("location");
    });

    it("labels both severities and renders messages as text", () => {
        const root = document.createElement("div");
        root.innerHTML = '<nav class="vst-editor-navigation"></nav>';
        const select = jest.fn<(index: number, section: string) => void>();
        renderEditorDiagnostics(
            root,
            [
                {
                    code: "model",
                    severity: "error",
                    clipIdx: 2,
                    message: "Choose a model.",
                },
                {
                    code: "warning",
                    severity: "warning",
                    message: "Keep <model> identity.",
                },
            ],
            select,
        );
        expect(root.querySelector("summary")?.textContent).toBe(
            "Authoring: 1 error · 1 warning",
        );
        expect(
            root.querySelector(".vst-editor-diagnostic-warning")?.textContent,
        ).toBe("Warning: Keep <model> identity.");
        expect(
            root.querySelector(".vst-editor-diagnostic-error")?.textContent,
        ).toBe("Error: Clip 2Choose a model.");
        root.querySelector<HTMLButtonElement>("button")?.click();
        expect(select).toHaveBeenCalledWith(2, "stages");
    });

    it("updates counts and messages while preserving an open disclosure", () => {
        const root = document.createElement("div");
        root.innerHTML = '<nav class="vst-editor-navigation"></nav>';
        const select = jest.fn<(index: number, section: string) => void>();
        renderEditorDiagnostics(
            root,
            [{ code: "first", severity: "error", message: "First issue" }],
            select,
        );
        const panel = root.querySelector<HTMLDetailsElement>("details");
        if (!panel) throw new Error("Missing diagnostics");
        panel.open = true;
        renderEditorDiagnostics(
            root,
            [
                {
                    code: "second",
                    severity: "warning",
                    message: "Second issue",
                },
                { code: "third", severity: "warning", message: "Third issue" },
            ],
            select,
        );
        expect(root.querySelector<HTMLDetailsElement>("details")?.open).toBe(
            true,
        );
        expect(root.querySelector("summary")?.textContent).toBe(
            "Authoring: 2 warnings",
        );
        expect(
            Array.from(
                root.querySelectorAll("li span"),
                (node) => node.textContent,
            ),
        ).toEqual(["Second issue", "Third issue"]);
    });
});
