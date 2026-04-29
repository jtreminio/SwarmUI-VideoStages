using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace VideoStages.WAN;

internal static class WanFirstLastFrameRewriter
{
    private const string WanImageToVideoType = "WanImageToVideo";
    private const string WanFirstLastFrameToVideoType = "WanFirstLastFrameToVideo";
    private const string ClipVisionEncodeType = "CLIPVisionEncode";

    internal static void TryRewriteToFirstLast(
        WorkflowGenerator g,
        JsonParser.StageSpec stage,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData wanEndImagePrepared)
    {
        if (!VideoStageModelCompat.SupportsWanFirstLastFrame(genInfo.VideoModel)
            || stage.ClipStageIndex != 0
            || stage.ClipRefs is not { Count: >= 2 }
            || wanEndImagePrepared is null)
        {
            return;
        }

        if (genInfo.PosCond is null || genInfo.PosCond.Count < 1)
        {
            Logs.Warning("VideoStages: WAN FLF rewrite skipped because conditioning output path was missing.");
            return;
        }

        string wanNodeId = $"{genInfo.PosCond[0]}";
        if (g.Workflow[wanNodeId] is not JObject wanNode
            || wanNode["inputs"] is not JObject wanInputs)
        {
            Logs.Warning("VideoStages: WAN FLF rewrite could not read the emitted WAN node.");
            return;
        }

        if (!StringUtils.NodeTypeMatches(wanNode, WanImageToVideoType))
        {
            return;
        }

        int width = ReadPositiveIntToken(
            wanInputs["width"],
            genInfo.Generator.UserInput.GetImageWidth());
        int height = ReadPositiveIntToken(
            wanInputs["height"],
            genInfo.Generator.UserInput.GetImageHeight());
        int length = genInfo.Frames ?? ReadPositiveIntToken(wanInputs["length"], 49);
        int batchSize = ReadPositiveBatchSize(wanInputs["batch_size"]);

        JArray scaledEndPath = ResolveScaledEndPath(
            g,
            wanInputs["start_image"] as JArray,
            wanEndImagePrepared,
            width,
            height);

        JToken clipVisionOut = wanInputs["clip_vision_output"];
        string flfNodeId;
        if (clipVisionOut is null || clipVisionOut.Type == JTokenType.Null)
        {
            flfNodeId = g.CreateNode(WanFirstLastFrameToVideoType, new JObject()
            {
                ["width"] = width,
                ["height"] = height,
                ["length"] = length,
                ["positive"] = wanInputs["positive"],
                ["negative"] = wanInputs["negative"],
                ["vae"] = wanInputs["vae"],
                ["start_image"] = wanInputs["start_image"],
                ["end_image"] = scaledEndPath,
                ["clip_vision_start_image"] = null,
                ["clip_vision_end_image"] = null,
                ["batch_size"] = batchSize
            });
        }
        else
        {
            if (clipVisionOut is not JArray clipVisionPath || clipVisionPath.Count < 2)
            {
                Logs.Warning("VideoStages: WAN FLF rewrite skipped because CLIP vision output wiring was unexpected.");
                return;
            }

            string encodeStartId = $"{clipVisionPath[0]}";
            if (g.Workflow[encodeStartId] is not JObject encodeStart
                || !StringUtils.NodeTypeMatches(encodeStart, ClipVisionEncodeType)
                || encodeStart["inputs"] is not JObject encodeInputs)
            {
                Logs.Warning("VideoStages: WAN FLF rewrite could not resolve CLIPVisionEncode for start vision.");
                return;
            }

            JToken clipLoader = encodeInputs["clip_vision"];
            string encodedEndId = g.CreateNode(ClipVisionEncodeType, new JObject()
            {
                ["clip_vision"] = clipLoader,
                ["image"] = scaledEndPath,
                ["crop"] = "center"
            });

            flfNodeId = g.CreateNode(WanFirstLastFrameToVideoType, new JObject()
            {
                ["width"] = width,
                ["height"] = height,
                ["length"] = length,
                ["positive"] = wanInputs["positive"],
                ["negative"] = wanInputs["negative"],
                ["vae"] = wanInputs["vae"],
                ["start_image"] = wanInputs["start_image"],
                ["clip_vision_start_image"] = clipVisionOut,
                ["end_image"] = scaledEndPath,
                ["clip_vision_end_image"] = WorkflowGenerator.NodePath(encodedEndId, 0),
                ["batch_size"] = batchSize
            });
        }

        genInfo.PosCond = new JArray(flfNodeId, 0);
        genInfo.NegCond = new JArray(flfNodeId, 1);
        g.Workflow.Remove(wanNodeId);
        g.CurrentMedia = g.CurrentMedia.WithPath(
            new JArray(flfNodeId, 2),
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat);
    }

    private static int ReadPositiveIntToken(JToken token, int fallback)
    {
        if (token is null || token.Type == JTokenType.Null || token is JArray)
        {
            return Math.Max(16, fallback);
        }

        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
        {
            return Math.Max(16, (int)Math.Round(token.Value<double>()));
        }

        return Math.Max(16, fallback);
    }

    private static int ReadPositiveBatchSize(JToken token)
    {
        if (token is null || token.Type == JTokenType.Null || token is JArray)
        {
            return 1;
        }

        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
        {
            return Math.Max(1, (int)Math.Round(token.Value<double>()));
        }

        return 1;
    }

    private static JArray ResolveScaledEndPath(
        WorkflowGenerator g,
        JArray startImagePath,
        WGNodeData wanEndImagePrepared,
        int width,
        int height)
    {
        if (CanReuseStartImagePath(g, startImagePath, wanEndImagePrepared?.Path, width, height))
        {
            return new JArray(startImagePath[0], startImagePath[1]);
        }

        string scaledEndId = g.CreateNode(NodeTypes.ImageScale, new JObject()
        {
            ["image"] = wanEndImagePrepared.Path,
            ["width"] = width,
            ["height"] = height,
            ["upscale_method"] = "lanczos",
            ["crop"] = "disabled"
        });
        return new JArray(scaledEndId, 0);
    }

    private static bool CanReuseStartImagePath(
        WorkflowGenerator g,
        JArray startImagePath,
        JArray endImageRawPath,
        int width,
        int height)
    {
        if (startImagePath is not { Count: 2 } || endImageRawPath is not { Count: 2 })
        {
            return false;
        }

        if (JToken.DeepEquals(startImagePath, endImageRawPath))
        {
            return true;
        }

        if (g.Workflow[$"{startImagePath[0]}"] is not JObject startNode
            || !StringUtils.NodeTypeMatches(startNode, NodeTypes.ImageScale)
            || startNode["inputs"] is not JObject startInputs
            || startInputs["image"] is not JArray scaledInput
            || !JToken.DeepEquals(scaledInput, endImageRawPath)
            || startInputs.Value<int?>("width") != width
            || startInputs.Value<int?>("height") != height)
        {
            return false;
        }

        return true;
    }
}
