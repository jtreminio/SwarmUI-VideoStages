import type { StageCapabilityView } from "../../architectures/policy";
import type { Clip, RootDefaults, Stage } from "../../types";
import type { DetailStripContext } from "../context";

export type StageCommit = (mutate: (target: Stage) => void) => void;
export type StageDebouncedCommit = (
    key: string,
    mutate: (target: Stage) => void,
) => void;
export type StageSliderBuilder = (
    label: string,
    focusKey: string,
    value: number,
    min: number,
    max: number,
    step: number,
    assign: (target: Stage, value: number) => void,
    options?: {
        title?: string;
        onValue?: (value: number) => void;
        help?: string;
        sliderMax?: number;
    },
) => HTMLElement;
export interface StagePanelBindings {
    context: DetailStripContext;
    clip: Clip;
    clipIdx: number;
    stageIdx: number;
    stage: Stage;
    defaults: RootDefaults;
    fields: HTMLElement;
    stageCapabilities: StageCapabilityView;
    commit: StageCommit;
    debouncedCommit: StageDebouncedCommit;
    slider: StageSliderBuilder;
}
