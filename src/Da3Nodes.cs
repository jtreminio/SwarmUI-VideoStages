using ComfyTyped.Core;
using ComfyTyped.Types;

namespace VideoStages;

// Hand-authored bindings for core ComfyUI's Depth Anything 3 nodes, which postdate the vendored
// ComfyTyped bindings. Shapes mirror the generated wrappers; the DA3Render "output" input is a
// dynamic combo whose nested fields ride in ExtraInputs ("output.normalization", …), matching the
// ResizeImageMaskNode precedent.

/// <summary>Marker for ComfyUI type "DA3_MODEL".</summary>
public sealed class Da3ModelType : IComfyType { public static string TypeName => "DA3_MODEL"; }

/// <summary>Marker for ComfyUI type "DA3_GEOMETRY".</summary>
public sealed class Da3GeometryType : IComfyType { public static string TypeName => "DA3_GEOMETRY"; }

public sealed class LoadDa3ModelNode : ComfyNode
{
    public const string ClassType = "LoadDA3Model";
    public override string ClassTypeName => ClassType;

    public NodeOutput<Da3ModelType> Model { get; }
    public NodeInput<StringType> ModelName { get; }
    public NodeInput<StringType> WeightDtype { get; }

    public LoadDa3ModelNode()
    {
        Model = AddOutput<Da3ModelType>(0, "DA3_MODEL");
        ModelName = AddInput<StringType>("model_name");
        WeightDtype = AddInput<StringType>("weight_dtype");
        WeightDtype.Set("default");
    }
}

public sealed class Da3InferenceNode : ComfyNode
{
    public const string ClassType = "DA3Inference";
    public override string ClassTypeName => ClassType;

    public NodeOutput<Da3GeometryType> Geometry { get; }
    public NodeInput<Da3ModelType> Da3Model { get; }
    public NodeInput<ImageType> Image { get; }
    public NodeInput<IntType> Resolution { get; }
    public NodeInput<StringType> ResizeMethod { get; }
    public NodeInput<StringType> Mode { get; }

    public Da3InferenceNode()
    {
        Geometry = AddOutput<Da3GeometryType>(0, "da3_geometry");
        Da3Model = AddInput<Da3ModelType>("da3_model");
        Image = AddInput<ImageType>("image");
        Resolution = AddInput<IntType>("resolution");
        Resolution.Set(504);
        ResizeMethod = AddInput<StringType>("resize_method");
        ResizeMethod.Set("upper_bound_resize");
        Mode = AddInput<StringType>("mode");
        Mode.Set("mono");
    }
}

public sealed class Da3RenderNode : ComfyNode
{
    public const string ClassType = "DA3Render";
    public override string ClassTypeName => ClassType;

    public NodeOutput<ImageType> IMAGE { get; }
    public NodeInput<Da3GeometryType> Geometry { get; }
    public NodeInput<StringType> Output { get; }

    public Da3RenderNode()
    {
        IMAGE = AddOutput<ImageType>(0, "IMAGE");
        Geometry = AddInput<Da3GeometryType>("da3_geometry");
        Output = AddInput<StringType>("output");
        Output.Set("depth");
    }
}
