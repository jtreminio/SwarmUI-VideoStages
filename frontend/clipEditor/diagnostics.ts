import type { AuthoringDiagnostic } from "../authoringDiagnostics";
import { actionButton, element } from "./components";

export function renderEditorDiagnostics(
    root: HTMLElement,
    diagnostics: readonly AuthoringDiagnostic[],
    selectClip: (index: number, section: string) => void,
): void {
    const previous = root.querySelector<HTMLDetailsElement>(
        ".vst-editor-diagnostics",
    );
    if (!diagnostics.length) {
        previous?.remove();
        return;
    }
    const panel = element("details", "vst-editor-diagnostics");
    panel.open = previous?.open ?? false;
    const errors = diagnostics.filter(
        (item) => item.severity === "error",
    ).length;
    const warnings = diagnostics.length - errors;
    const counts = [
        errors ? `${errors} error${errors === 1 ? "" : "s"}` : "",
        warnings ? `${warnings} warning${warnings === 1 ? "" : "s"}` : "",
    ].filter(Boolean);
    const summary = element("summary", "", `Authoring: ${counts.join(" · ")}`);
    panel.append(summary);
    const list = element("ul", "vst-editor-diagnostic-list");
    for (const item of diagnostics) {
        const row = element("li", `vst-editor-diagnostic-${item.severity}`);
        row.dataset.vstDiagnostic = item.code;
        row.append(
            element(
                "strong",
                "",
                `${item.severity === "error" ? "Error" : "Warning"}: `,
            ),
        );
        if (item.clipIdx !== undefined) {
            const clipIdx = item.clipIdx;
            row.append(
                actionButton({
                    key: `diagnostic-${item.code}-${clipIdx}`,
                    label: `Clip ${clipIdx}`,
                    title: `Open clip ${clipIdx} settings`,
                    run: () => selectClip(clipIdx, "stages"),
                }),
            );
        }
        row.append(element("span", "", item.message));
        list.append(row);
    }
    panel.append(list);
    if (previous) previous.replaceWith(panel);
    else root.querySelector(".vst-editor-navigation")?.after(panel);
}
