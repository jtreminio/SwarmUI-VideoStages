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
        bool nodesAvailable = generator.Features.Contains(Ltx2HostIntegration.FeatureFlag);
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
                        $"clip {clip.ClipId} keyframe",
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
                // Runtime resolves every uploaded drive through UploadedMedia, whose Get* entry
                // points treat an unreadable payload as a bug, so each kind is checked here first.
                // The plan compiler already drops an Upload drive with no payload
                // (ltx2.ic-lora.drive-media-missing); a DriveData.None entry still carries a null
                // Upload, so the filter is a null guard.
                foreach (IcLoraPlan icLora in icLoras.Where(
                    entry => entry.Drive.Source == IcLoraMediaSourceKind.Upload
                        && !string.IsNullOrWhiteSpace(entry.Drive.Upload?.Data)))
                {
                    string data = icLora.Drive.Upload.Data;
                    string fileName = icLora.Drive.Upload.FileName;
                    PlanDiagnostic unreadable = icLora.Drive.MediaKind switch
                    {
                        IcLoraDriveMediaKind.Audio => media.AudioDiagnostic(
                            data, fileName, clip.ClipId, stage.StageId),
                        IcLoraDriveMediaKind.Image => media.ImageDiagnostic(
                            data, fileName, IcLoraDriveDescriptor.Image(clip.ClipId), clip.ClipId, stage.StageId),
                        IcLoraDriveMediaKind.Video => media.VideoDiagnostic(
                            data, fileName, IcLoraDriveDescriptor.Video(clip.ClipId), clip.ClipId, stage.StageId),
                        _ => null,
                    };
                    if (unreadable is not null)
                    {
                        diagnostics.Add(unreadable);
                    }
                }
            }
        }
        return diagnostics;
    }
}
