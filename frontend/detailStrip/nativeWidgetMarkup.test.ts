/// <reference types="node" />
import * as fs from "node:fs";
import * as path from "node:path";
import { beforeEach, describe, expect, it } from "@jest/globals";
import {
    detailBody,
    detailStripHarness,
    fieldByLabel,
    sliderNumberByLabel,
} from "../__test_helpers__/detailStrip";
import { setSelection } from "../selection";

describe("detail strip native widget markup", () => {
    const { setup } = detailStripHarness();

    it("uses SwarmUI's native full-width flex class for every stage slider", () => {
        setup([
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
        for (const label of ["Steps", "CFG Scale", "Frame Ref R0"]) {
            expect(
                sliderNumberByLabel(label)
                    .closest(".vst-stage-slider")
                    ?.classList.contains("auto-input-flex-wide"),
            ).toBe(true);
        }
    });

    // ---- #2: native-widget markup + dock-override CSSOM probe -------------
    // The dock is built from SwarmUI's own `.auto-input` and `.input-group`
    // markup, and deliberately does not override their geometry.
    describe("CSSOM probe", () => {
        // The main checkout reaches host wwwroot at ../../../wwwroot; inside a
        // git worktree the extension is nested deeper, so walk up for it.
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
        const injectDockCss = (): void =>
            injectCss(
                "vst-probe-css",
                path.join(__dirname, "..", "..", "Assets", "video-stages.css"),
            );

        const computed = (el: Element): CSSStyleDeclaration =>
            window.getComputedStyle(el);
        // jsdom's getComputedStyle is document-order only — it honours neither
        // specificity nor `!important`, so a declaration that only matters
        // because it outranks the theme has to be read off the rule itself.
        const dockRule = (selector: string): CSSStyleRule | null => {
            const sheet = (
                document.getElementById(
                    "vst-probe-css",
                ) as HTMLStyleElement | null
            )?.sheet;
            for (const rule of Array.from(sheet?.cssRules ?? [])) {
                if (
                    rule instanceof CSSStyleRule &&
                    rule.selectorText === selector
                ) {
                    return rule;
                }
            }
            return null;
        };

        beforeEach(() => {
            setup([
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

        it("(a) emits native SwarmUI `.auto-input` widget markup for every field type", () => {
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
            expect(durInput?.classList.contains("auto-number")).toBe(true);
            expect(
                fieldByLabel("Duration (s)").classList.contains(
                    "auto-number-box",
                ),
            ).toBe(true);
            const skipRow = document.querySelector<HTMLElement>(
                ".vst-detail .vst-detail-field-check",
            );
            expect(skipRow?.classList.contains("auto-checkbox-box")).toBe(true);
            expect(
                skipRow
                    ?.querySelector("input")
                    ?.classList.contains("auto-checkbox"),
            ).toBe(true);

            setSelection({ kind: "prompt-major", clipIdx: 0 });
            const editor = document.querySelector<HTMLTextAreaElement>(
                ".vst-detail .vst-prompt-editor",
            );
            expect(editor?.classList.contains("auto-text")).toBe(true);
            expect(editor?.classList.contains("auto-text-block")).toBe(true);
        });

        it("(b) leaves field geometry native while adding the scoped outline", () => {
            injectHostCss();
            injectDockCss();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            const model = fieldByLabel("Model");
            expect(model.classList.contains("auto-input-flex")).toBe(true);
            expect(computed(model).display).toBe("flex");
            const dropdown = model.querySelector(".auto-dropdown");
            expect(dropdown).not.toBeNull();
            if (dropdown) {
                expect(computed(dropdown).width).toBe("auto");
            }
            const durationInput =
                fieldByLabel("Duration (s)").querySelector("input");
            expect(durationInput).not.toBeNull();
            if (durationInput) {
                expect(computed(durationInput).backgroundColor).toBe(
                    "var(--background)",
                );
            }
            // Only text-like inputs take the dock's opaque background; the
            // theme's specialized checkbox/radio/range rendering is spared.
            const specialized = Array.from(
                document.querySelectorAll<HTMLInputElement>(
                    '.vst-detail input[type="checkbox"], .vst-detail input[type="radio"], .vst-detail input[type="range"]',
                ),
            );
            expect(specialized.length).toBeGreaterThanOrEqual(2);
            for (const input of specialized) {
                expect(computed(input).backgroundColor).not.toBe(
                    "var(--background)",
                );
            }
            expect(computed(detailBody() as HTMLElement).marginBottom).toBe(
                "6px",
            );
            const activeStage = document.querySelector<HTMLElement>(
                '[data-vst-repeater-key="stages"] > .input-group-content > .input-group-open',
            );
            expect(activeStage).not.toBeNull();
            if (activeStage) {
                expect(computed(activeStage).marginBottom).toBe("0px");
                expect(Number.parseFloat(computed(activeStage).minWidth)).toBe(
                    0,
                );
                expect(computed(activeStage).borderLeftWidth).toBe("2px");
                const headerWrap = activeStage.querySelector<HTMLElement>(
                    ":scope > .vst-detail-repeating-group-header > .header-label-wrap",
                );
                expect(headerWrap).not.toBeNull();
                if (headerWrap) {
                    expect(computed(headerWrap).minHeight).toBe("26px");
                }
                const content = activeStage.querySelector<HTMLElement>(
                    ":scope > .vst-detail-repeating-group-content",
                );
                expect(content).not.toBeNull();
                if (content) {
                    expect(computed(content).paddingLeft).toBe("7px");
                }
            }
            const outlinedItems = Array.from(
                document.querySelectorAll<HTMLElement>(
                    ".vst-detail-section > .vst-detail-section-content > .vst-detail-repeating-group",
                ),
            );
            expect(outlinedItems.length).toBeGreaterThanOrEqual(4);
            for (const nestedItem of outlinedItems) {
                expect(computed(nestedItem).borderLeftWidth).toBe("2px");
            }
            const loraWeightRow = document.querySelector<HTMLElement>(
                ".vst-stage-lora-weight-row",
            );
            const loraWeightLabel =
                loraWeightRow?.querySelector<HTMLElement>(
                    ".vst-detail-field-label",
                ) ?? null;
            const loraWeightLabelElement =
                loraWeightRow?.querySelector<HTMLLabelElement>("label") ?? null;
            expect(loraWeightRow).not.toBeNull();
            expect(loraWeightLabel).not.toBeNull();
            expect(loraWeightLabelElement).not.toBeNull();
            if (loraWeightRow && loraWeightLabel && loraWeightLabelElement) {
                expect(computed(loraWeightRow).flexWrap).toBe("nowrap");
                expect(computed(loraWeightLabelElement).flexGrow).toBe("1");
                expect(
                    Number.parseFloat(
                        computed(loraWeightLabelElement).minWidth,
                    ),
                ).toBe(0);
                expect(computed(loraWeightLabel).width).not.toBe("0px");
                expect(computed(loraWeightLabel).textOverflow).toBe("ellipsis");
            }
            const subsectionHeader = document.querySelector<HTMLElement>(
                ".vst-detail-subsection-crumb",
            );
            expect(subsectionHeader).not.toBeNull();
            if (subsectionHeader) {
                expect(
                    subsectionHeader.classList.contains("vst-detail-crumb"),
                ).toBe(true);
                expect(computed(subsectionHeader).textTransform).toBe(
                    "uppercase",
                );
                expect(computed(subsectionHeader).borderBottomWidth).toBe(
                    "1px",
                );
            }
        });

        it("clears the open-group minimum width only inside the detail dock", () => {
            injectHostCss("punked.css");
            const outsideDock = document.createElement("div");
            outsideDock.className = "input-group input-group-open";
            document.body.appendChild(outsideDock);
            // Punked has a floor to override. Without one this proves nothing.
            const hostMinWidth = computed(outsideDock).minWidth;
            expect(Number.parseFloat(hostMinWidth)).toBeGreaterThan(0);

            injectDockCss();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });

            const activeStage = document.querySelector<HTMLElement>(
                '[data-vst-repeater-key="stages"] > .input-group-content > .input-group-open',
            );
            expect(activeStage).not.toBeNull();
            if (activeStage) {
                expect(Number.parseFloat(computed(activeStage).minWidth)).toBe(
                    0,
                );
            }
            expect(computed(outsideDock).minWidth).toBe(hostMinWidth);

            const openGroup = dockRule(
                ".vst-detail .input-group.input-group-open",
            );
            expect(openGroup).not.toBeNull();
            if (openGroup) {
                expect(openGroup.style.getPropertyValue("min-width")).toBe("0");
                expect(openGroup.style.getPropertyPriority("min-width")).toBe(
                    "important",
                );
            }
        });

        it("(d) wraps prompt textareas in the host's wide text-field row", () => {
            injectHostCss();
            injectDockCss();
            const assertNativePrompt = (): void => {
                const ta = document.querySelector<HTMLElement>(
                    ".vst-detail .vst-detail-prompt",
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
                    "input-group-content",
                );
            };

            setSelection({ kind: "prompt-major", clipIdx: 0 });
            assertNativePrompt();
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            assertNativePrompt();
        });

        it("(e) uses native groups for both sections and repeatable items", () => {
            injectHostCss();
            injectDockCss();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const sections = document.querySelectorAll(
                ".vst-detail-body > .vst-detail-section.input-group",
            );
            expect(sections.length).toBeGreaterThan(1);
            expect(
                document.querySelector(
                    ".vst-detail-section .vst-detail-repeating-group.input-group",
                ),
            ).not.toBeNull();
        });

        it("(c) emits the matching native row variant across every panel", () => {
            injectHostCss();
            injectDockCss();
            const panels: (() => void)[] = [
                () => setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 }),
                () => setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 }),
                () => setSelection({ kind: "audio", clipIdx: 0 }),
                () => setSelection({ kind: "prompt-major", clipIdx: 0 }),
                () =>
                    setSelection({
                        kind: "prompt-minor",
                        clipIdx: 0,
                        windowIdx: 0,
                    }),
            ];
            for (const select of panels) {
                select();
                for (const field of document.querySelectorAll(
                    ".vst-detail .auto-input",
                )) {
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
