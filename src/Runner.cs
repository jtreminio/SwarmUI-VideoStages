using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Abstractions;

namespace VideoStages;

// Orchestration map — phases run in priority order during WorkflowGenerator.Generate.
// Registrations live in VideoStagesExtension.OnInit; numeric priorities in Constants.WorkflowStepPriority.
//
// #  Pri    Phase                                         Reads                                          Writes / clears
// -  -----  --------------------------------------------  ---------------------------------------------  ----------------------------------------------------
// 1  -6.0   PreflightRequest                              compiled plan, backend features               — (must stay non-mutating)
// 2  -5.9   CaptureCoreVideoControlNetPreprocessors       core ControlNet graph                          captures raw image/audio/apply facts,
//                                                                                                        then fans out architecture interpretation
// 3  -4.2   CaptureBase                                   —                                              architecture reference capture
// 4   5.89  CaptureRefiner                                —                                              architecture reference capture
// 5  10.95  CapturePreCoreVideoMedia                      —                                              architecture pre-core capture,
//                                                                                                        videostages.arch.{id}.pre-core-node-ids
// 6  11.05  DropCoreImageToVideoOutput                    architecture pre-core state,                  clears both above
//                                                         videostages.arch.{id}.pre-core-node-ids
// 7  11.4   ApplyRootAudioMaskDimensionsAfterNativeVideo  —                                              —
// 8  11.5   RunConfiguredStages                           architecture references,                     executes planned architecture sessions
//                                                         videostages.controlnet.fullimage.{i}
//
// Phase 1 is the only place a request may be rejected for a missing dependency: every later phase
// mutates the host graph, so a failure past it leaves the user with a broken workflow.
public static class Runner
{
    public static void PreflightRequest(WorkflowGenerator g)
    {
        if (!IsExtensionActive(g))
        {
            return;
        }

        g.GetVideoExecutionPlanContext()?.PrepareRequest();
    }

    public static void CaptureCoreVideoControlNetPreprocessors(WorkflowGenerator g)
    {
        if (!TryGetPreparedActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        Dispatch(context, ArchitectureHostPhase.CaptureControlNetPreprocessors);
    }

    public static void CaptureBase(WorkflowGenerator g)
    {
        if (!TryGetPreparedActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        Dispatch(context, ArchitectureHostPhase.CaptureBaseReference);
    }

    public static void CaptureRefiner(WorkflowGenerator g)
    {
        if (!TryGetPreparedActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        Dispatch(context, ArchitectureHostPhase.CaptureRefinerReference);
    }

    public static void CapturePreCoreVideoMedia(WorkflowGenerator g)
    {
        if (!TryGetPreparedActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        Dispatch(context, ArchitectureHostPhase.CapturePreCoreMedia);
    }

    public static void DropCoreImageToVideoOutput(WorkflowGenerator g)
    {
        if (!TryGetPreparedActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        Dispatch(context, ArchitectureHostPhase.DropCoreOutput);
    }

    public static void ApplyRootAudioMaskDimensionsAfterNativeVideo(WorkflowGenerator g)
    {
        if (!TryGetPreparedActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        Dispatch(context, ArchitectureHostPhase.ApplyRootAudioMaskDimensions);
    }

    public static void RunConfiguredStages(WorkflowGenerator g)
    {
        if (!TryGetPreparedActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        context.RequirePreparedExecutionHost().RunConfiguredStages();
    }

    private static void Dispatch(
        VideoExecutionPlanContext context,
        ArchitectureHostPhase phase) =>
        context.RequirePreparedExecutionHost().DispatchHostPhase(phase);

    private static bool IsExtensionActive(WorkflowGenerator g) => VideoStagesPromptSection.IsActive(g);

    private static bool TryGetPreparedActiveExecution(
        WorkflowGenerator g,
        out VideoExecutionPlanContext context)
    {
        if (!IsExtensionActive(g))
        {
            context = null;
            return false;
        }
        context = g.GetVideoExecutionPlanContext();
        if (context is null)
        {
            return false;
        }
        context.RequirePrepared();
        return true;
    }
}
