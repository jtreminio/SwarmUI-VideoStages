using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;

namespace VideoStages.CharacterRefine;

internal static class BonitaGraph
{
    internal static string[] MissingNodeClassTypes(WorkflowGenerator generator) =>
        BonitaIntegration.NodeRequirements
            .Where(requirement => !generator.Features.Contains(requirement.Feature))
            .Select(requirement => requirement.ClassType)
            .ToArray();

    internal static (BonitaFindAndCropNode Node, WGNodeData Media) Crop(
        WorkflowGenerator generator,
        WGNodeData source,
        CharacterRefineSettingsPlan plan,
        JArray identityImage = null)
    {
        if (source?.Path is not JArray sourcePath)
        {
            throw Invariant.Failure(
                "Make Them Bonita requires decoded video input.");
        }

        int blur = generator.UserInput.Get(T2IParamTypes.SegmentMaskBlur, 10);
        using WorkflowBridge bridge = BridgeSync.For(generator);
        JArray finderPath;
        if (plan.Finder == "yolo")
        {
            BonitaIntegration.EnsureYoloModels(generator);
            BonitaYOLOFinderNode finder = bridge.AddNode(
                new BonitaYOLOFinderNode().With(
                    SubjectModel: BonitaIntegration.YoloSubjectModel,
                    PartModel: BonitaIntegration.YoloPartModel,
                    SubjectConfidence: plan.SubjectConfidence,
                    PartConfidence: plan.PartConfidence));
            finderPath = finder.Finder.ToPath();
        }
        else
        {
            BonitaIntegration.EnsureSamCheckpoint(generator);
            CheckpointLoaderSimpleNode samLoader = bridge.AddNode(
                new CheckpointLoaderSimpleNode().With(
                    CkptName: BonitaIntegration.SamCheckpointRelativePath));
            BonitaSAM3FinderNode finder = bridge.AddNode(
                new BonitaSAM3FinderNode().With(
                    SubjectConfidence: plan.SubjectConfidence,
                    PartConfidence: plan.PartConfidence));
            finder.Sam3Model.ConnectTo(samLoader.MODEL);
            finder.Sam3Clip.ConnectTo(samLoader.CLIP);
            finderPath = finder.Finder.ToPath();
        }

        BonitaFindAndCropNode crop = bridge.AddNode(
            new BonitaFindAndCropNode().With(
                Subject: plan.Subject,
                Part: plan.Part,
                CropWidth: plan.CropWidth,
                CropHeight: plan.CropHeight,
                SurroundingArea: BonitaIntegration.SurroundingArea,
                TrackedAreaBlur: blur));
        crop.VideoFrames.ConnectFromPath(bridge, sourcePath);
        crop.Finder.ConnectFromPath(bridge, finderPath);
        if (identityImage is not null)
        {
            crop.CharacterReference.ConnectFromPath(bridge, identityImage);
        }

        WGNodeData media = source.WithPath(
            crop.CropsToRepair.ToPath(),
            WGNodeData.DT_VIDEO);
        media.Width = plan.CropWidth;
        media.Height = plan.CropHeight;
        media.AttachedAudio = null;
        return (crop, media);
    }

    internal static WGNodeData Apply(
        WorkflowGenerator generator,
        WGNodeData source,
        BonitaFindAndCropNode crop,
        JArray repairedCrops)
    {
        if (source?.Path is not JArray sourcePath)
        {
            throw Invariant.Failure(
                "Make Them Bonita requires decoded video input.");
        }
        using WorkflowBridge bridge = BridgeSync.For(generator);
        BonitaApplyRepairNode finish = bridge.AddNode(new BonitaApplyRepairNode());
        finish.VideoFrames.ConnectFromPath(bridge, sourcePath);
        finish.RepairedCrops.ConnectFromPath(bridge, repairedCrops);
        finish.PlacementData.ConnectTo(crop.PlacementData);
        return source.WithPath(
            finish.RepairedVideoFrames.ToPath(),
            WGNodeData.DT_VIDEO);
    }

}
