import { captureAuthoringTransactionSnapshot } from "../authoringSnapshot";
import {
    clipRelationship,
    SOURCE_AUDIO_RELATIONSHIP,
    SOURCE_VIDEO_RELATIONSHIP,
} from "../clipRelationships";
import { executableBoundaryForLeftClip } from "../clipSemantics";
import { showHostActionMenu } from "../host/swarmUiAdapters";
import { isVideoStagesEnabled } from "../swarmInputs";
import { formatSecondsTenth } from "../timelineDetail";
import { resolveTimelineTiming } from "../timelineTiming";
import { buildToggleSwitch } from "../toggleSwitch";
import type { AuthoringDocument } from "../types";
import { actionButton, element } from "./components";

import type { EditorNavigation } from "./view";

export function flowMap(
    state: AuthoringDocument,
    selected: number,
    navigation: EditorNavigation,
    collapsed: boolean,
): HTMLElement {
    const strip = element("aside", "vst-editor-strip");
    strip.dataset.testid = "clip-strip";
    strip.setAttribute("aria-label", "Clip flow");
    const output = element("div", "vst-editor-output");
    const enabled = isVideoStagesEnabled();
    strip.dataset.enabled = String(enabled);
    const timing = resolveTimelineTiming(
        state.clips,
        state.fps,
        captureAuthoringTransactionSnapshot().capabilities,
    );
    const status = element("div", "vst-editor-status");
    const statusLabel = element("label", "");
    statusLabel.append(element("strong", "", enabled ? "Enabled" : "Disabled"));
    const toggle = buildToggleSwitch({
        checked: enabled,
        title: enabled ? "Disable VideoStages" : "Enable VideoStages",
        onChange: navigation.setEnabled,
    });
    const input = toggle.querySelector("input");
    if (input) {
        statusLabel.htmlFor = input.id;
        input.dataset.testid = "toggle-enabled";
        input.setAttribute("role", "switch");
        input.setAttribute("aria-checked", String(enabled));
        input.setAttribute(
            "aria-describedby",
            "vst-editor-disabled-description",
        );
    }
    status.append(statusLabel, toggle);
    const dimensions = actionButton({
        key: "output-settings",
        label: `${state.width}×${state.height} · ${state.fps} fps`,
        run: () => navigation.jump("output"),
    });
    dimensions.title = "Edit output dimensions and frame rate";
    const duration = element(
        "span",
        "vst-editor-output-duration",
        `${formatSecondsTenth(timing.outputSeconds)} output`,
    );
    duration.dataset.testid = "output-duration";
    duration.title =
        "Final output length, accounting for joins and skipped clips";
    const collapse = actionButton({
        key: "toggle-map",
        label: collapsed ? "»" : "«",
        run: navigation.toggleMap,
    });
    collapse.classList.add("vst-editor-collapse");
    collapse.setAttribute(
        "aria-label",
        collapsed ? "Expand clip map" : "Collapse clip map",
    );
    collapse.setAttribute("aria-expanded", String(!collapsed));
    collapse.setAttribute("aria-controls", "vst-editor-sequence");
    status.append(collapse);
    dimensions.classList.add("vst-editor-dimensions", "btn-primary");
    const dimensionText = dimensions.textContent || "";
    dimensions.textContent = "";
    const compactDimensions = element("span", "vst-editor-dimensions-compact");
    compactDimensions.append(
        element("span", "", `${state.width}×${state.height}`),
        element("span", "", `${state.fps} fps`),
    );
    dimensions.append(
        element("span", "vst-editor-dimensions-label", dimensionText),
        compactDimensions,
    );
    dimensions.setAttribute("aria-label", `Output settings: ${dimensionText}`);
    output.append(status, dimensions);
    const description = element(
        "span",
        "visually-hidden",
        enabled
            ? "VideoStages is enabled."
            : "VideoStages disabled. Use the switch above to enable. Editing is still available.",
    );
    description.id = "vst-editor-disabled-description";
    const sequence = element("div", "vst-editor-sequence");
    sequence.id = "vst-editor-sequence";
    sequence.dataset.testid = "clip-sequence";
    sequence.setAttribute("aria-label", "Video clips");
    strip.append(output, description, sequence);
    state.clips.forEach((clip, index) => {
        const relationship = clipRelationship(state.clips, index);
        const sourceUsage =
            relationship === SOURCE_VIDEO_RELATIONSHIP
                ? "video"
                : relationship === SOURCE_AUDIO_RELATIONSHIP
                  ? "audio"
                  : "";
        const sourceDescription = sourceUsage
            ? `Source ${sourceUsage} for Clip ${index + 1}; this clip does not extend the final video`
            : "";
        const card = element("div", "vst-editor-clip");
        card.dataset.clipId = clip.id;
        card.dataset.clipIndex = String(index);
        card.dataset.testid = "clip-card";
        card.classList.toggle("selected", index === selected);
        card.dataset.sourceUsage = sourceUsage;
        card.dataset.mediaKind = clip.audioOnly ? "audio" : "video";
        card.title = [
            sourceDescription,
            index > 0
                ? "Shift-click to delete this clip"
                : "The first clip cannot be deleted",
        ]
            .filter(Boolean)
            .join(". ");
        card.addEventListener(
            "mousedown",
            (event) => {
                // Shift extends a text selection before the click can delete.
                if (event.shiftKey && event.button === 0)
                    event.preventDefault();
            },
            { capture: true },
        );
        card.addEventListener(
            "click",
            (event) => {
                if (event.shiftKey) {
                    event.preventDefault();
                    event.stopPropagation();
                    navigation.deleteClip(index);
                } else if (!(event.target as HTMLElement).closest("button")) {
                    navigation.selectClip(index);
                }
            },
            { capture: true },
        );
        const header = element("div", "vst-editor-clip-heading");
        const role = clip.skipped
            ? " · skipped"
            : sourceUsage
              ? ` · ${sourceUsage} source`
              : timing.consumedClipIndexes.includes(index)
                ? " · input only"
                : clip.audioOnly
                  ? " · audio only"
                  : "";
        const choose = actionButton({
            key: "select-clip",
            label: String(index),
            run: () => navigation.selectClip(index),
        });
        choose.setAttribute("aria-current", String(index === selected));
        choose.setAttribute("aria-label", `Select Clip ${index}${role}`);
        choose.textContent = "";
        choose.append(element("span", "vst-editor-clip-index", String(index)));
        if (role)
            header.append(
                element("span", "vst-editor-clip-label", role.slice(3)),
            );
        header.append(
            actionButton({
                key: "clip-stages",
                label: `${clip.stages.length} stage${clip.stages.length === 1 ? "" : "s"}`,
                run: () => navigation.selectClip(index, "stages"),
            }),
            element(
                "span",
                "vst-editor-duration",
                formatSecondsTenth(clip.duration),
            ),
        );
        const prompt = actionButton({
            key: "clip-prompt",
            label: `Prompt · ${clip.prompt || "Using global prompt"}`,
            run: () => navigation.selectClip(index, "prompt", true),
        });
        prompt.classList.add("vst-editor-prompt-link");
        prompt.title = clip.prompt || "Using global prompt";
        prompt.setAttribute("aria-label", `Edit clip ${index} prompt`);
        if (index > 0) {
            const remove = actionButton({
                key: "delete-clip",
                label: "×",
                run: () => navigation.deleteClip(index),
            });
            remove.classList.add("vst-editor-delete-clip");
            remove.setAttribute("aria-label", `Delete Clip ${index}`);
            remove.title = `Delete Clip ${index} · Shift-click also deletes`;
            header.append(remove);
        }
        if (state.clips.length > 1) {
            const actions: { key: string; action(): void }[] = [];
            if (index > 0) {
                actions.push(
                    {
                        key: `Delete Clip ${index}`,
                        action: () => navigation.deleteClip(index),
                    },
                    {
                        key: "Move earlier",
                        action: () => navigation.moveClip(index, index - 1),
                    },
                );
            }
            if (index < state.clips.length - 1) {
                actions.push({
                    key: "Move later",
                    action: () => navigation.moveClip(index, index + 1),
                });
            }
            const more = actionButton({
                key: "clip-actions",
                label: "…",
                run: () => showHostActionMenu(more, actions),
            });
            more.classList.add("vst-editor-clip-actions");
            more.setAttribute("aria-label", `Actions for Clip ${index}`);
            more.setAttribute("aria-haspopup", "true");
            more.title = `Clip ${index} actions`;
            card.append(more);
        }
        const media = element(
            "small",
            "vst-editor-clip-media",
            sourceUsage
                ? `${sourceUsage} source`
                : clip.audioOnly
                  ? "audio only"
                  : clip.skipped
                    ? "skipped"
                    : "video",
        );
        card.append(choose, header, prompt, media);
        sequence.append(card);
        if (index < state.clips.length - 1) {
            const nextClipIdx = sourceUsage
                ? index + 1
                : (executableBoundaryForLeftClip(state.clips, index)
                      ?.rightIdx ?? index + 1);
            const join = actionButton({
                key: "edit-join",
                label: `↓ ${sourceUsage ? `Source ${sourceUsage}` : relationship} ↓`,
                run: () => navigation.selectClip(nextClipIdx, "join"),
            });
            join.dataset.leftClipId = clip.id;
            join.dataset.sourceUsage = sourceUsage;
            join.dataset.mediaKind = sourceUsage;
            join.title = sourceDescription || "Edit how these clips join";
            join.classList.add("vst-editor-join");
            join.textContent = "";
            const joinLabel = element("span", "vst-editor-join-label");
            joinLabel.append(
                element("span", "vst-editor-join-arrow", "↓ "),
                element(
                    "span",
                    "",
                    sourceUsage ? `Source ${sourceUsage}` : relationship,
                ),
                element("span", "vst-editor-join-arrow", " ↓"),
            );
            join.append(joinLabel);
            if (sourceUsage) {
                join.append(
                    element("span", "vst-editor-join-hint", " Feeds next"),
                );
                join.setAttribute(
                    "aria-label",
                    `Source ${sourceUsage}; feeds Clip ${index + 1}; excluded from output`,
                );
            }
            sequence.append(join);
        }
    });
    sequence.append(
        actionButton({
            key: "add-clip",
            label: "+ Add clip",
            run: navigation.addClip,
        }),
    );
    const footer = element("footer", "vst-editor-result");
    footer.dataset.testid = "flow-output";
    const included = timing.outputClipIndexes.map((index) => `Clip ${index}`);
    footer.append(
        duration,
        element(
            "span",
            "vst-editor-result-clips",
            included.join(" + ") || "No output clips",
        ),
    );
    strip.append(footer);
    return strip;
}
