import type { CapabilityViewResolver } from "../architectures/policy";
import { setSelection } from "../selection";
import { parseIntAttr } from "../trackDomUtils";
import {
    createDetailSelectionDomainOperations,
    type DetailSelectionDomainOperations,
    type StructuralCommit,
} from "./selectionDomainOperations";

const STAGE_SELECTOR = "[data-vst-stage]";
const MODEL_SELECTOR = "[data-vst-model]";
const INTERACTIVE_SELECTOR = `${STAGE_SELECTOR}, ${MODEL_SELECTOR}`;

export interface DetailSelectionOperations
    extends DetailSelectionDomainOperations {
    onMouseDownCapture(event: MouseEvent): void;
    onClickCapture(event: MouseEvent): void;
    onKeyDownCapture(event: KeyboardEvent): void;
    onStripKeyDown(event: KeyboardEvent): void;
}

export const createDetailSelectionOperations = (
    structuralCommit: StructuralCommit,
    getCapabilities: () => CapabilityViewResolver,
    renderAfterExternalCommand: () => void = () => {},
): DetailSelectionOperations => {
    const domain = createDetailSelectionDomainOperations(
        structuralCommit,
        getCapabilities,
        renderAfterExternalCommand,
    );
    const handleActivation = (target: Element, shiftKey: boolean): void => {
        const stageChip = target.closest(STAGE_SELECTOR);
        if (stageChip instanceof HTMLElement) {
            const clipIdx = parseIntAttr(stageChip, "data-clip-idx");
            const stageIdx = parseIntAttr(stageChip, "data-stage-idx");
            if (clipIdx === null || stageIdx === null) return;
            if (shiftKey) {
                domain.deleteStage(clipIdx, stageIdx);
            } else {
                domain.selectStage(clipIdx, stageIdx);
            }
            return;
        }
        const modelBadge = target.closest(MODEL_SELECTOR);
        if (modelBadge instanceof HTMLElement) {
            const clipIdx = parseIntAttr(modelBadge, "data-clip-idx");
            if (clipIdx !== null) domain.selectStage(clipIdx, 0);
        }
    };

    return {
        ...domain,
        onMouseDownCapture: (event) => {
            if (
                event.target instanceof Element &&
                event.target.closest(INTERACTIVE_SELECTOR)
            ) {
                event.stopPropagation();
            }
        },
        onClickCapture: (event) => {
            if (
                !(event.target instanceof Element) ||
                !event.target.closest(INTERACTIVE_SELECTOR)
            ) {
                return;
            }
            event.stopPropagation();
            handleActivation(event.target, event.shiftKey);
        },
        onKeyDownCapture: (event) => {
            if (event.key !== "Enter" && event.key !== " ") return;
            if (
                !(event.target instanceof Element) ||
                !event.target.closest(INTERACTIVE_SELECTOR)
            ) {
                return;
            }
            event.preventDefault();
            event.stopPropagation();
            handleActivation(event.target, event.shiftKey);
        },
        onStripKeyDown: (event) => {
            if (
                event.key !== "Escape" ||
                (event.target instanceof Element &&
                    event.target.closest(".sui-popover"))
            ) {
                return;
            }
            event.preventDefault();
            event.stopPropagation();
            setSelection({ kind: "none" });
        },
    };
};
