import type { Clip } from "../types";

export interface ProjectedStage {
    stage: Clip["stages"][number];
    rawIndex: number;
}

export interface ProjectedClipEntry {
    clip: Clip;
    index: number;
    stages: ProjectedStage[];
}
