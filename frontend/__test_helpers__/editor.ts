import { createClipEditor } from "../clipEditor/clipEditor";
import { EDITOR_BODY_ID } from "../clipEditor/tab";
import { authoringFixtureHarness, type ClipFixture } from "./authoringFixtures";
import { mountCheckbox } from "./dom";

export {
    type ClipFixture,
    commitNumber,
    committedClips,
    modelGlobals,
    RETAKE_SOURCE,
    type StageFixture,
    swarmGlobals,
    type WindowFixture,
} from "./authoringFixtures";

export const mountEditorHost = (): void => {
    mountCheckbox("input_group_content_videostages_toggle", { checked: true });
    document.body.insertAdjacentHTML(
        "beforeend",
        '<ul id="bottombartabcollection"><li><a href="#Tools-Tab">Tools</a></li></ul><div id="t2i_bottom_bar_content"></div>',
    );
};

export const editorRoot = (): HTMLElement => {
    const root = document.getElementById(EDITOR_BODY_ID);
    if (!root) throw new Error("clip editor is not mounted");
    return root;
};

export const editorSection = (key: string): HTMLElement => {
    const section = editorRoot().querySelector<HTMLElement>(
        `[data-section="${key}"]`,
    );
    if (!section) throw new Error(`Missing editor section: ${key}`);
    return section;
};

export const editorItem = (key: string, index: number): HTMLElement => {
    const item = editorSection(key).querySelectorAll<HTMLElement>(
        '[data-testid="editor-item"]',
    )[index];
    if (!item) throw new Error(`Missing editor item: ${key}/${index}`);
    return item;
};

export const editorFieldContainer = (
    section: string,
    label: string,
    itemIndex?: number,
): HTMLElement => {
    const scope =
        itemIndex === undefined
            ? editorSection(section)
            : editorItem(section, itemIndex);
    const field = Array.from(
        scope.querySelectorAll<HTMLElement>(".vst-form-field, .auto-input"),
    ).find((node) => {
        const name = node.querySelector<HTMLElement>(
            ".vst-detail-field-label, .auto-input-name",
        );
        return (
            name?.firstChild?.textContent?.trim() === label ||
            name?.textContent?.trim() === label
        );
    });
    if (!field)
        throw new Error(
            `Missing editor field container: ${section}/${itemIndex ?? "*"}/${label}`,
        );
    return field;
};

export const editorField = <T extends HTMLElement = HTMLInputElement>(
    section: string,
    label: string,
    selector = "input",
    itemIndex?: number,
): T => {
    const control = editorFieldContainer(
        section,
        label,
        itemIndex,
    ).querySelector<T>(selector);
    if (!control)
        throw new Error(
            `Missing editor field: ${section}/${itemIndex ?? "*"}/${label}/${selector}`,
        );
    return control;
};

export const editorHarness = () => {
    let editor: ReturnType<typeof createClipEditor> | null = null;
    const disposeEditor = (): void => {
        editor?.dispose();
        editor = null;
    };
    const fixture = authoringFixtureHarness(disposeEditor);
    const setupFixtures = (fixtures: ClipFixture[], loras?: string[]): void => {
        disposeEditor();
        fixture.setupFixtures(fixtures, loras);
        mountEditorHost();
    };
    return {
        setupFixtures,
        setup: (fixtures: ClipFixture[], loras?: string[]): HTMLElement => {
            setupFixtures(fixtures, loras);
            editor = createClipEditor();
            editor.init();
            return editorRoot();
        },
        get root() {
            return editorRoot();
        },
        get editor() {
            if (!editor) throw new Error("clip editor is not initialized");
            return editor;
        },
        get saveSpy() {
            return fixture.saveSpy;
        },
        get refreshSpy() {
            return fixture.refreshSpy;
        },
        section: editorSection,
        item: editorItem,
        field: editorField,
        fieldContainer: editorFieldContainer,
        disposeEditor,
    };
};
