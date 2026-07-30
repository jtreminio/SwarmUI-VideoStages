using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Planning;
using Image = SwarmUI.Utils.Image;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Materializes WAN's bounded first/final frame references. Native WAN nodes accept concrete
/// images at those two positions; arbitrary timeline-frame insertion remains an LTX feature.
/// </summary>
internal sealed class WanFrameReferenceResolver(WorkflowGenerator generator)
{
    internal WGNodeData ResolveFirst(WanFrameReferencePlan reference)
    {
        Image image = MaterializeUpload(reference, "WAN first-frame reference");
        return image is null
            ? null
            : generator.LoadImage(image, "${videostageswanfirstframe}", false);
    }

    internal Image ResolveLast(WanFrameReferencePlan reference) =>
        MaterializeUpload(reference, "WAN final-frame reference");

    private Image MaterializeUpload(WanFrameReferencePlan reference, string descriptor)
    {
        if (reference is null)
        {
            return null;
        }
        if (!StringUtils.Equals(reference.Source, "Upload"))
        {
            PlanDiagnosticReporter.TrackRequestWarning(
                generator.UserInput,
                $"VideoStages: {descriptor} source '{reference.Source}' cannot be materialized "
                    + "by the native WAN image input; ignoring it for this generation.");
            return null;
        }
        ImageFile image = ImageReference.MaterializeUploadedRefImage(
            generator,
            reference.InlineData,
            reference.UploadFileName,
            descriptor);
        return image as Image;
    }
}
