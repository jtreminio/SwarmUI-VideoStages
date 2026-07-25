using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;

namespace VideoStages;

// Orchestration map — phases run in priority order during WorkflowGenerator.Generate.
// Registrations live in VideoStagesExtension.OnInit; numeric priorities in Constants.WorkflowStepPriority.
//
// #  Pri    Phase                                         Reads                                          Writes / clears
// -  -----  --------------------------------------------  ---------------------------------------------  ----------------------------------------------------
// 1  -6.0   PreflightRequest                              compiled plan, backend features               — (must stay non-mutating)
// 2  -5.9   CaptureCoreVideoControlNetPreprocessors       —                                              writes videostages.controlnet.fullimage.{i}
// 3  -4.2   CaptureBase                                   —                                              architecture reference capture
// 4   5.9   CaptureRefiner                                —                                              architecture reference capture
// 5  10.95  CapturePreCoreVideoMedia                      —                                              architecture pre-core capture,
//                                                                                                                videostages.pre-core-node-ids
// 6  11.05  DropCoreImageToVideoOutput                    architecture pre-core state,                  clears both above
//                                                         videostages.pre-core-node-ids
// 7  11.4   ApplyRootAudioMaskDimensionsAfterNativeVideo  —                                              —
// 8  11.5   RunConfiguredStages                           architecture references,                     executes planned architecture sessions
//                                                         videostages.controlnet.fullimage.{i}
//
// Phase 1 is the only place a request may be rejected for a missing dependency: every later phase
// mutates the host graph, so a failure past it leaves the user with a broken workflow.
//
// Non-phase entry point (NOT registered as a workflow step — called from outside the pipeline):
//   GetRootMediaResizer  architecture-selected sizing for static AltImageToVideo handlers.
public static class Runner
{
    public static void PreflightRequest(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g))
        {
            return;
        }

        new VideoArchitectureExecutionHost(g).PreflightRequest();
    }

    public static void CaptureCoreVideoControlNetPreprocessors(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g))
        {
            return;
        }

        Dispatch(g, ArchitectureHostPhase.CaptureControlNetPreprocessors);
    }

    public static void CaptureBase(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g) || !HasConfiguredStages(g))
        {
            return;
        }

        Dispatch(g, ArchitectureHostPhase.CaptureBaseReference);
    }

    public static void CaptureRefiner(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g) || !HasConfiguredStages(g))
        {
            return;
        }

        Dispatch(g, ArchitectureHostPhase.CaptureRefinerReference);
    }

    public static void CapturePreCoreVideoMedia(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g))
        {
            return;
        }

        Dispatch(g, ArchitectureHostPhase.CapturePreCoreMedia);
    }

    public static void DropCoreImageToVideoOutput(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g))
        {
            return;
        }

        Dispatch(g, ArchitectureHostPhase.DropCoreOutput);
    }

    public static void ApplyRootAudioMaskDimensionsAfterNativeVideo(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g))
        {
            return;
        }

        Dispatch(g, ArchitectureHostPhase.ApplyRootAudioMaskDimensions);
    }

    public static void RunConfiguredStages(WorkflowGenerator g)
    {
        if (!RequireSupportedActiveExecution(g))
        {
            return;
        }

        new VideoArchitectureExecutionHost(g).RunConfiguredStages();
    }

    // --- Non-phase entry points (not registered as workflow steps; see header map) ---

    internal static IArchitectureRootMediaResizer GetRootMediaResizer(WorkflowGenerator g) =>
        new VideoArchitectureExecutionHost(g).GetRootMediaResizer();

    private static void Dispatch(WorkflowGenerator g, ArchitectureHostPhase phase) =>
        new VideoArchitectureExecutionHost(g).DispatchHostPhase(phase);

    private static bool IsExtensionActive(WorkflowGenerator g) => VideoStagesPromptSection.IsActive(g);

    private static bool RequireSupportedActiveExecution(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g))
        {
            return false;
        }
        _ = g.RequireVideoExecutionPlanContext();
        return true;
    }

    private static bool HasConfiguredStages(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g))
        {
            return false;
        }

        return g.RequireVideoExecutionPlanContext().Plan.Clips.Any(
            clip => clip.Stages.Count > 0);
    }
}
