using System.Collections.Immutable;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Verifies the custom nodes that planned LTX stages will emit before graph mutation begins.
/// </summary>
internal static class Ltx2RequestPreflight
{
    internal static IReadOnlyList<PlanDiagnostic> Resolve(
        WorkflowGenerator generator,
        VideoExecutionPlan plan)
    {
        List<PlanDiagnostic> diagnostics = [];
        UploadedMediaPreflight media = new(generator.UserInput);
        bool nodesAvailable = Ltx2HostIntegration.IsAvailable(generator.Features);
        bool reportedMissingNodes = false;
        foreach (ClipPlan clip in plan.Clips.Where(
            clip => clip.Architecture.Id == Ltx2ArchitectureModule.ArchitectureId))
        {
            foreach (StagePlan stage in clip.Stages)
            {
                foreach (FrameRefPlan reference in
                    stage.RequireLtx2Payload().FrameReferences.Where(
                        entry => entry.SourceKind == FrameRefSourceKind.Upload))
                {
                    if (media.ImageDiagnostic(
                        reference.InlineData,
                        reference.UploadFileName,
                        $"clip {clip.ClipId} frame reference",
                        clip.ClipId,
                        stage.StageId) is { } unreadable)
                    {
                        diagnostics.Add(unreadable);
                    }
                }
                ImmutableArray<IcLoraPlan> icLoras = stage.RequireLtx2Payload().IcLoras;
                if (icLoras.IsDefaultOrEmpty)
                {
                    continue;
                }
                if (!nodesAvailable && !reportedMissingNodes)
                {
                    reportedMissingNodes = true;
                    diagnostics.Add(new(
                        PlanDiagnosticSeverity.Error,
                        "ltx2.iclora.nodes-missing",
                        "IC-LoRAs require the ComfyUI-LTXVideo custom nodes. "
                        + $"Install {Ltx2HostIntegration.NodeUrl} "
                        + "or use SwarmUI's LTXVideo feature installer.",
                        clip.ClipId,
                        stage.StageId));
                }
                // Only an uploaded audio-kind drive reaches the media materializer; a video-kind
                // upload is handed to the host's base64 video loader untouched.
                foreach (IcLoraPlan icLora in icLoras.Where(
                    entry => entry.HasAudioReference
                        && entry.Drive.Source == IcLoraMediaSourceKind.Upload
                        && entry.Drive.MediaKind == IcLoraDriveMediaKind.Audio))
                {
                    if (media.AudioDiagnostic(
                        icLora.Drive.Upload?.Data,
                        icLora.Drive.Upload?.FileName,
                        clip.ClipId,
                        stage.StageId) is { } unreadable)
                    {
                        diagnostics.Add(unreadable);
                    }
                }
            }
        }
        return diagnostics;
    }
}
