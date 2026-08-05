using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

// Orchestration map — phases run in priority order during WorkflowGenerator.Generate.
// Registrations live in VideoStagesExtension.OnInit; numeric priorities in Constants.WorkflowStepPriority.
//
// #  Pri    Phase                                         Reads                                          Writes / clears
// -  -----  --------------------------------------------  ---------------------------------------------  ----------------------------------------------------
// 1  -6.0   PreflightRequest                              compiled plan, backend features                — (must stay non-mutating)
// 2  -5.9   CaptureCoreVideoControlNetPreprocessors       core ControlNet graph                          captures raw image/audio/apply facts,
//                                                                                                        then fans out architecture interpretation
// 3  -4.2   CaptureBase                                   —                                              architecture reference capture
// 4   5.89  CaptureRefiner                                —                                              architecture reference capture
// 5  10.95  CapturePreCoreVideoMedia                      eligible generated-root media/VAE, graph        in-memory root snapshot
// 6  11.05  DropCoreImageToVideoOutput                    captured root state                            restores root and prunes core video pass
// 7  11.4   ApplyRootAudioMaskDimensionsAfterNativeVideo  root stage resolution, graph                   resizes audio SolidMask nodes to root dims
// 8  11.5   RunConfiguredStages                           architecture references, phase 2 captures      executes planned architecture sessions
//
// Phase 1 is the only place a request may be rejected for a missing dependency: every later phase
// mutates the host graph, so a failure past it leaves the user with a broken workflow.
public static class Runner
{
    public static void PreflightRequest(WorkflowGenerator g)
    {
        if (!DocumentJson.IsActive(g))
        {
            return;
        }

        g.GetVideoExecutionPlanContext()?.PrepareRequest();
    }

    public static void CaptureCoreVideoControlNetPreprocessors(WorkflowGenerator g)
    {
        if (!TryGetActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        context.CaptureControlNetPreprocessors();
    }

    public static void CaptureBase(WorkflowGenerator g)
    {
        if (!TryGetActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        context.CaptureBaseReference();
    }

    public static void CaptureRefiner(WorkflowGenerator g)
    {
        if (!TryGetActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        context.CaptureRefinerReference();
    }

    public static void CapturePreCoreVideoMedia(WorkflowGenerator g)
    {
        if (!TryGetActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        context.CapturePreCoreMedia();
    }

    public static void DropCoreImageToVideoOutput(WorkflowGenerator g)
    {
        if (!TryGetActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        context.DropCoreOutput();
    }

    public static void ApplyRootAudioMaskDimensionsAfterNativeVideo(WorkflowGenerator g)
    {
        if (!TryGetActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        context.ApplyRootAudioMaskDimensions();
    }

    public static void RunConfiguredStages(WorkflowGenerator g)
    {
        if (!TryGetActiveExecution(g, out VideoExecutionPlanContext context))
        {
            return;
        }

        context.RunConfiguredStages();
    }

    private static bool TryGetActiveExecution(
        WorkflowGenerator g,
        out VideoExecutionPlanContext context)
    {
        if (!DocumentJson.IsActive(g))
        {
            context = null;
            return false;
        }
        context = g.GetVideoExecutionPlanContext();
        return context is not null;
    }
}
