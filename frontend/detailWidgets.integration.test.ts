/// <reference types="node" />
import * as fs from "node:fs";
import * as path from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "@jest/globals";
import { editorHarness } from "./__test_helpers__/editor";
import { setSelection } from "./selection";

describe("native widgets in the production editor", () => {
    const h = editorHarness();
    const fieldByLabel = (label: string): HTMLElement => {
        const control = h.field(
            label === "Duration (s)" ? "prompt" : "stages",
            label,
            "input, select, textarea",
        );
        const field = control.closest<HTMLElement>(".auto-input");
        if (!field) throw new Error(`Missing native field row: ${label}`);
        return field;
    };

    it("uses SwarmUI's native full-width flex class for every stage slider", () => {
        h.setup([
            {
                duration: 4,
                frameRefs: [{ source: "Upload", frame: 0 }],
                stages: [
                    {
                        loras: [{ name: "lora-x.safetensors", weight: 0.7 }],
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        for (const label of ["Steps", "CFG Scale", "Keyframe 0"]) {
            expect(
                h
                    .field("stages", label, ".auto-slider-number")
                    .closest(".vst-stage-slider")
                    ?.classList.contains("auto-input-flex-wide"),
            ).toBe(true);
        }
    });

    describe("CSSOM probe", () => {
        afterEach(() => {
            document
                .querySelectorAll('style[id^="vst-probe-"]')
                .forEach((style) => {
                    style.remove();
                });
        });
        // Worktrees can nest the extension deeper than the main checkout.
        const wwwrootDir = ((): string => {
            let dir = __dirname;
            for (;;) {
                const candidate = path.join(dir, "wwwroot");
                if (fs.existsSync(candidate)) {
                    return candidate;
                }
                const parent = path.dirname(dir);
                if (parent === dir) {
                    return path.resolve(__dirname, "..", "..", "..", "wwwroot");
                }
                dir = parent;
            }
        })();
        const injectCss = (id: string, filePath: string): void => {
            const css = fs.readFileSync(filePath, "utf8");
            const style = document.createElement("style");
            style.id = id;
            style.textContent = css;
            document.head.appendChild(style);
        };
        const injectHostCss = (theme = "modern.css"): void => {
            injectCss(
                "vst-probe-host-css",
                path.join(wwwrootDir, "css", "site.css"),
            );
            injectCss(
                "vst-probe-theme-css",
                path.join(wwwrootDir, "css", "themes", theme),
            );
        };
        const injectEditorCss = (): void =>
            injectCss(
                "vst-probe-css",
                path.join(__dirname, "..", "Assets", "video-stages.css"),
            );

        const computed = (el: Element): CSSStyleDeclaration =>
            window.getComputedStyle(el);
        // jsdom ignores specificity and !important; inspect theme overrides directly.
        const editorRule = (selector: string): CSSStyleRule | null => {
            const normalizedSelector = selector.replace(/\s+/g, " ");
            const sheet = (
                document.getElementById(
                    "vst-probe-css",
                ) as HTMLStyleElement | null
            )?.sheet;
            for (const rule of Array.from(sheet?.cssRules ?? [])) {
                if (
                    rule instanceof CSSStyleRule &&
                    rule.selectorText.replace(/\s+/g, " ") ===
                        normalizedSelector
                ) {
                    return rule;
                }
            }
            return null;
        };

        beforeEach(() => {
            h.setup([
                {
                    duration: 10,
                    stages: [
                        {
                            loras: [
                                {
                                    name: "lora-x.safetensors",
                                    weight: 0.7,
                                },
                            ],
                        },
                        {},
                    ],
                    frameRefs: [{ source: "Base", frame: 1 }],
                    windows: [{ start: 1, duration: 2, prompt: "w" }],
                },
            ]);
        });

        it("provides native theme tokens to editor and body-mounted media surfaces", () => {
            injectEditorCss();
            for (const className of [
                "vst-clip-editor",
                "vst-trim-modal",
                "vst-media-preview-modal",
            ]) {
                const surface = document.createElement("div");
                surface.className = className;
                document.body.appendChild(surface);
                const style = computed(surface);
                expect(style.getPropertyValue("--vst-panel")).toBe(
                    "var(--background-panel, var(--background-gray))",
                );
                expect(style.getPropertyValue("--vst-hairline")).toBe(
                    "color-mix(in srgb, var(--light-border) 60%, transparent)",
                );
                expect(style.getPropertyValue("--vst-radius")).toBe("5px");
                expect(style.getPropertyValue("--vst-font-sm")).toBe("0.857em");
                surface.remove();
            }
        });

        it("highlights the production Add action as a file drop target", () => {
            injectEditorCss();
            const rule = editorRule(
                '#videostages-editor .vst-editor-actions [data-action="add"]:not(:disabled).vst-file-drop-hover',
            );
            expect(rule?.style.getPropertyValue("border-color")).toBe(
                "var(--emphasis)",
            );
            expect(rule?.style.getPropertyValue("color")).toBe(
                "var(--emphasis)",
            );
            expect(rule?.style.getPropertyValue("background")).toBe(
                "color-mix(in srgb, var(--emphasis) 24%, transparent)",
            );
        });

        it("bounds diagnostics so fields retain editing space", () => {
            injectEditorCss();
            const diagnostics = editorRule(".vst-editor-diagnostics");
            expect(diagnostics?.style.getPropertyValue("max-height")).toBe(
                "35%",
            );
            expect(diagnostics?.style.getPropertyValue("overflow")).toBe(
                "auto",
            );
        });

        it("places reference tags above fields with a separate action column", () => {
            injectEditorCss();
            const row = editorRule(
                '.vst-editor-section[data-section="references"] .vst-editor-row',
            );
            expect(row?.style.getPropertyValue("grid-template-columns")).toBe(
                "minmax(0, 1fr) auto",
            );
            const fields = editorRule(
                '.vst-editor-section[data-section="references"] .vst-editor-row > .vst-editor-fields',
            );
            expect(fields?.style.getPropertyValue("grid-column")).toBe(
                "1 / -1",
            );
        });

        it("reserves a full-width catalog row above the available editor space", () => {
            injectEditorCss();
            expect(
                editorRule(".vst-clip-editor")?.style.getPropertyValue(
                    "grid-template-rows",
                ),
            ).toBe("minmax(0, 1fr)");
            expect(
                editorRule(
                    ".vst-clip-editor:has(> .vst-catalog-notice)",
                )?.style.getPropertyValue("grid-template-rows"),
            ).toBe("auto minmax(0, 1fr)");
            expect(
                editorRule(
                    ".vst-clip-editor > .vst-catalog-notice, .vst-clip-editor > .vst-catalog-status",
                )?.style.getPropertyValue("grid-column"),
            ).toBe("1 / -1");
            const status = editorRule(".vst-clip-editor > .vst-catalog-status");
            expect(status?.style.getPropertyValue("min-height")).toBe("0");
            expect(status?.style.getPropertyValue("overflow")).toBe("auto");
        });

        it("emits native SwarmUI `.auto-input` widget markup for every field type", () => {
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            const modelSelect = fieldByLabel("Model").querySelector("select");
            expect(modelSelect?.classList.contains("auto-dropdown")).toBe(true);
            const modelRow = fieldByLabel("Model");
            expect(modelRow.classList.contains("auto-input")).toBe(true);
            expect(modelRow.classList.contains("auto-dropdown-box")).toBe(true);
            expect(
                modelRow.querySelector(".auto-input-name")?.textContent,
            ).toBe("Model");
            const durInput =
                fieldByLabel("Duration (s)").querySelector("input");
            expect(durInput?.classList.contains("auto-slider-number")).toBe(
                true,
            );
            expect(
                fieldByLabel("Duration (s)").classList.contains(
                    "auto-slider-box",
                ),
            ).toBe(true);
            const skipRow = document.querySelector<HTMLElement>(
                "#videostages-editor .vst-detail-field-check",
            );
            expect(skipRow?.classList.contains("auto-checkbox-box")).toBe(true);
            expect(
                skipRow
                    ?.querySelector("input")
                    ?.classList.contains("auto-checkbox"),
            ).toBe(true);

            setSelection({ kind: "prompt-major", clipIdx: 0 });
            const editor = document.querySelector<HTMLTextAreaElement>(
                "#videostages-editor textarea",
            );
            expect(editor?.classList.contains("auto-text")).toBe(true);
            expect(editor?.classList.contains("auto-text-block")).toBe(true);
        });

        it("keeps native fields inside consistently padded stage cards", () => {
            injectHostCss();
            injectEditorCss();
            const model = fieldByLabel("Model");
            expect(model.classList.contains("auto-input-flex")).toBe(true);
            const card = editorRule(
                "#videostages-editor .vst-editor-card.input-group-content",
            );
            expect(card?.style.padding).toBe("16px");
            expect(card?.style.background).toBe("var(--background-panel)");
            const fields = editorRule(
                "#videostages-editor.vst-clip-editor .vst-form-field",
            );
            expect(fields?.style.margin).toBe("0");
            expect(fields?.style.padding).toBe("0");
            const subsection = editorRule(
                "#videostages-editor .vst-detail-subsection-crumb",
            );
            expect(subsection?.style.getPropertyValue("border-top")).toBe(
                "1px solid var(--border-color)",
            );
        });

        it("fits open groups into the editor while preserving the host theme minimum", () => {
            injectHostCss("punked.css");
            const outsideEditor = document.createElement("div");
            outsideEditor.className = "input-group input-group-open";
            document.body.appendChild(outsideEditor);
            const hostMinWidth = computed(outsideEditor).minWidth;
            expect(Number.parseFloat(hostMinWidth)).toBeGreaterThan(0);
            injectEditorCss();
            const section = document.querySelector<HTMLElement>(
                "#videostages-editor .vst-editor-section",
            );
            expect(section).toBeInstanceOf(HTMLElement);
            if (!section) throw new Error("Stage section is missing");
            expect(Number.parseFloat(computed(section).minWidth)).toBe(0);
            expect(computed(outsideEditor).minWidth).toBe(hostMinWidth);
            const openGroup = editorRule(
                ".vst-clip-editor .input-group.input-group-open",
            );
            expect(openGroup?.style.getPropertyValue("min-width")).toBe("0");
            expect(openGroup?.style.getPropertyPriority("min-width")).toBe(
                "important",
            );
        });

        it("keeps Add actions next to section titles with native button targets", () => {
            injectEditorCss();
            const actions = editorRule(
                ".vst-editor-section-heading > .vst-editor-actions",
            );
            expect(actions?.style.getPropertyValue("margin-left")).toBe("0");
            expect(actions?.style.getPropertyValue("flex-shrink")).toBe("0");
            expect(
                editorRule(
                    "#videostages-editor :is(.vst-editor-actions, .vst-stage-lora-row) .basic-button",
                )?.style.getPropertyValue("min-height"),
            ).toBe("32px");
        });

        it.each([
            [
                "keeps output controls outside vertical scrolling",
                ".vst-editor-output",
                "flex",
                "0 0 auto",
            ],
            [
                "lays nested production fields out horizontally",
                ".vst-editor-fields, .vst-clip-editor .vst-detail-fields",
                "display",
                "flex",
            ],
            [
                "isolates editor scrolling",
                ".vst-editor-scroll",
                "overflow",
                "auto",
            ],
            [
                "contains wheel scrolling",
                ".vst-editor-scroll",
                "overscroll-behavior",
                "contain",
            ],
            [
                "stacks clips in the map",
                ".vst-editor-strip",
                "flex-direction",
                "column",
            ],
            [
                "scrolls long clip sequences vertically",
                ".vst-editor-sequence",
                "overflow",
                "hidden auto",
            ],
            [
                "places the clip map beside the editor",
                ".vst-clip-editor",
                "grid-template-columns",
                "var(--vst-editor-rail-width) minmax(0, 1fr)",
            ],
            [
                "keeps section navigation independently scrollable",
                ".vst-editor-navigation",
                "overflow",
                "auto hidden",
            ],
            [
                "allows the final section to reach the top",
                ".vst-editor-section:last-child",
                "min-height",
                "100%",
            ],
            [
                "identifies a section while scrolling",
                ".vst-clip-editor .vst-editor-section-heading.input-group-header",
                "position",
                "sticky",
            ],
            [
                "gives shared cards a consistent border",
                "#videostages-editor .vst-editor-card.input-group-content",
                "border",
                "1px solid var(--border-color)",
            ],
            [
                "uses the theme for selected clips",
                ".vst-editor-clip.selected",
                "border-color",
                "var(--emphasis)",
            ],
            [
                "truncates compact prompt shortcuts",
                ".vst-editor-clip .vst-editor-prompt-link",
                "text-overflow",
                "ellipsis",
            ],
            [
                "allows prompt resizing",
                ".vst-clip-editor .vst-form-field textarea",
                "resize",
                "vertical",
            ],
            [
                "preserves conditional control visibility",
                ".vst-clip-editor [hidden]",
                "display",
                "none",
            ],
        ])("%s", (_name, selector, property, value) => {
            injectEditorCss();
            expect(editorRule(selector)?.style.getPropertyValue(property)).toBe(
                value,
            );
        });

        it("keeps source usage labels visible beside join actions", () => {
            injectEditorCss();
            const link = editorRule(
                '.vst-editor-join:not([data-source-usage=""])',
            );
            expect(link?.style.getPropertyValue("display")).toBe("flex");
            expect(link?.style.getPropertyValue("gap")).toBe("8px");
            expect(
                editorRule(".vst-editor-join-hint")?.style.getPropertyValue(
                    "white-space",
                ),
            ).toBe("nowrap");
        });

        it("gives join actions a keyboard focus indicator", () => {
            injectEditorCss();
            const focus = editorRule(
                ".vst-clip-editor :focus-visible:not(input, select, textarea)",
            );
            expect(focus?.style.getPropertyValue("outline")).toBe(
                "2px solid var(--emphasis)",
            );
            expect(focus?.style.getPropertyValue("outline-offset")).toBe("2px");
        });

        it("wraps prompt textareas in the host's wide text-field row", () => {
            injectHostCss();
            injectEditorCss();
            const assertNativePrompt = (section: string): void => {
                const ta = document.querySelector<HTMLElement>(
                    `#videostages-editor [data-section="${section}"] textarea`,
                );
                expect(ta).not.toBeNull();
                if (!ta) {
                    return;
                }
                expect(computed(ta).width).toBe("100%");
                const row = ta.closest(".auto-input");
                expect(row?.classList.contains("auto-text-box")).toBe(true);
                expect(row?.classList.contains("auto-input-flex-wide")).toBe(
                    true,
                );
                expect(row?.parentElement?.classList).toContain(
                    "vst-editor-fields",
                );
            };

            setSelection({ kind: "prompt-major", clipIdx: 0 });
            assertNativePrompt("prompt");
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            assertNativePrompt("relay-prompts");
        });

        it("uses native groups for both sections and repeatable items", () => {
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const sections = document.querySelectorAll(
                "#videostages-editor .vst-editor-section.input-group",
            );
            expect(sections.length).toBeGreaterThan(1);
            expect(
                document.querySelector(
                    "#videostages-editor .vst-editor-card.input-group-content",
                ),
            ).not.toBeNull();
        });

        it("emits the matching native row variant across every panel", () => {
            for (const key of [
                "stages",
                "keyframes",
                "audio",
                "prompt",
                "relay-prompts",
            ]) {
                const rows = Array.from(
                    h.section(key).querySelectorAll(".auto-input"),
                );
                expect(rows.length).toBeGreaterThan(0);
                for (const field of rows) {
                    expect(
                        field.classList.contains("auto-slider-box") ||
                            field.classList.contains("auto-file-box") ||
                            field.classList.contains("auto-input-flex") ||
                            field.classList.contains("auto-input-flex-wide"),
                    ).toBe(true);
                }
            }
        });
    });
});
