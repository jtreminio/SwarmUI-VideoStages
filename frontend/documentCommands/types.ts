import type { GeneratedEntryMode } from "../architectures/generatedFeatures";
import type {
    ArchitectureModelCatalog,
    ArchitectureRetargetPlan,
} from "../architectures/types";
import type {
    CanonicalAudioTrack,
    CanonicalAudioTrackSpan,
    CanonicalClip,
    CanonicalClipReference,
    CanonicalFrameRefImage,
    CanonicalPromptWindow,
    CanonicalRetake,
    CanonicalStage,
    CanonicalVideoStagesConfig,
} from "../types";
import type {
    AudioSpanPatch,
    AudioTrackPatch,
    ClipPatch,
    ClipReferencePatch,
    PromptWindowPatch,
    RefPatch,
    RetakePatch,
    RootSettingsPatch,
    StagePatch,
} from "./listEntities";

export type CommandFailure =
    | "missing-target"
    | "duplicate-id"
    | "invalid-id"
    | "retake-already-exists"
    | "invalid-operation"
    | "invalid-architecture-conversion"
    | "architecture-invariant";

export interface DocumentCommandResult {
    document: CanonicalVideoStagesConfig;
    applied: boolean;
    failure?: CommandFailure;
}

export type DocumentCommand =
    | { type: "batch"; commands: readonly DocumentCommand[] }
    | { type: "root.patch"; patch: RootSettingsPatch }
    | {
          type: "clip.add";
          clip: CanonicalClip;
          beforeClipId?: string | null;
      }
    | { type: "clip.remove"; clipId: string }
    | { type: "clip.toggle-skip"; clipId: string }
    | {
          type: "clip.move";
          clipId: string;
          beforeClipId: string | null;
      }
    | { type: "clip.patch"; clipId: string; patch: ClipPatch }
    | {
          type: "clip.convert-architecture";
          clipId: string;
          target: ArchitectureRetargetPlan;
      }
    | {
          type: "stage.add";
          clipId: string;
          stage: CanonicalStage;
          beforeStageId?: string | null;
      }
    | {
          type: "stage.retarget-model";
          clipId: string;
          stageId: string;
          target: Pick<
              ArchitectureRetargetPlan,
              "architectureId" | "modelProfileId" | "model"
          >;
      }
    | { type: "stage.remove"; clipId: string; stageId: string }
    | { type: "stage.toggle-skip"; clipId: string; stageId: string }
    | {
          type: "stage.move";
          clipId: string;
          stageId: string;
          beforeStageId: string | null;
      }
    | {
          type: "stage.patch";
          clipId: string;
          stageId: string;
          patch: StagePatch;
      }
    | {
          type: "ref.add";
          clipId: string;
          ref: CanonicalFrameRefImage;
          beforeRefId?: string | null;
      }
    | { type: "ref.remove"; clipId: string; refId: string }
    | {
          type: "ref.move";
          clipId: string;
          refId: string;
          beforeRefId: string | null;
      }
    | { type: "ref.patch"; clipId: string; refId: string; patch: RefPatch }
    | {
          type: "clip-reference.add";
          clipId: string;
          reference: CanonicalClipReference;
          beforeReferenceId?: string | null;
      }
    | { type: "clip-reference.remove"; clipId: string; referenceId: string }
    | {
          type: "clip-reference.move";
          clipId: string;
          referenceId: string;
          beforeReferenceId: string | null;
      }
    | {
          type: "clip-reference.patch";
          clipId: string;
          referenceId: string;
          patch: ClipReferencePatch;
      }
    | {
          type: "prompt-window.add";
          clipId: string;
          window: CanonicalPromptWindow;
          beforeWindowId?: string | null;
      }
    | {
          type: "prompt-window.remove";
          clipId: string;
          windowId: string;
      }
    | {
          type: "prompt-window.move";
          clipId: string;
          windowId: string;
          beforeWindowId: string | null;
      }
    | {
          type: "prompt-window.patch";
          clipId: string;
          windowId: string;
          patch: PromptWindowPatch;
      }
    | { type: "retake.add"; clipId: string; retake: CanonicalRetake }
    | { type: "retake.remove"; clipId: string; retakeId: string }
    | {
          type: "retake.patch";
          clipId: string;
          retakeId: string;
          patch: RetakePatch;
      }
    | {
          type: "audio-track.add";
          track: CanonicalAudioTrack;
          beforeTrackId?: string | null;
      }
    | { type: "audio-track.remove"; trackId: string }
    | {
          type: "audio-track.move";
          trackId: string;
          beforeTrackId: string | null;
      }
    | {
          type: "audio-track.patch";
          trackId: string;
          patch: AudioTrackPatch;
      }
    | {
          type: "audio-span.add";
          trackId: string;
          span: CanonicalAudioTrackSpan;
          beforeSpanId?: string | null;
      }
    | { type: "audio-span.remove"; trackId: string; spanId: string }
    | {
          type: "audio-span.move";
          trackId: string;
          spanId: string;
          beforeSpanId: string | null;
      }
    | {
          type: "audio-span.patch";
          trackId: string;
          spanId: string;
          patch: AudioSpanPatch;
      };

export interface DocumentCommandContext {
    /** The current catalog snapshot that authoritatively owns model identity. */
    architectureCatalog: ArchitectureModelCatalog | null;
    /** The current host input mode used by a generated clip root. */
    generatedEntryMode?: GeneratedEntryMode;
}
