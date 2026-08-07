using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Authoring;
using VideoStages.Generated;

namespace VideoStages.Architectures.Ltx2.Runtime.Stage;

internal sealed class LtxModelPromptPreparer(WorkflowGenerator g)
{
    private const double PromptRelayEpsilon = 0.001;

    internal void Prepare(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageContext stageContext,
        WGNodeData sourceMedia)
    {
        g.FinalLoadedModel = genInfo.VideoModel;
        (genInfo.VideoModel, genInfo.Model, WGNodeData clip, genInfo.Vae) = g.CreateModelLoader(
            genInfo.VideoModel,
            "image2video",
            null,
            true,
            sectionId: genInfo.ContextID);

        int width = sourceMedia.Width ?? g.UserInput.GetImageWidth();
        int height = sourceMedia.Height ?? g.UserInput.GetImageHeight();
        int steps = genInfo.Steps;
        double guidance = g.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1);
        string positivePrompt = PromptText.SelectVideoOrGlobalPrompt(genInfo.Prompt);
        string negativePrompt = PromptText.SelectVideoOrGlobalPrompt(genInfo.NegativePrompt);

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput clipOutput = bridge.ResolvePath(clip.Path);

        SwarmClipTextEncodeAdvancedNode negCondNode = AddSwarmClipTextEncodeAdvanced(
            bridge, clipOutput, steps, negativePrompt, width, height, guidance,
            stageContext?.Claim.Negative);
        genInfo.NegCond = negCondNode.CONDITIONING.ToPath();

        if (TryBuildPromptRelay(
                bridge, genInfo, stageContext, clipOutput, positivePrompt, sourceMedia,
                out string overridePositive))
        {
            return;
        }

        // Not reached in relay mode, which supplies its own positive conditioning; core's node then
        // goes unclaimed and is swept as before.
        SwarmClipTextEncodeAdvancedNode posCondNode = AddSwarmClipTextEncodeAdvanced(
            bridge, clipOutput, steps, overridePositive ?? positivePrompt, width, height, guidance,
            stageContext?.Claim.Positive);
        genInfo.PosCond = posCondNode.CONDITIONING.ToPath();
    }

    private bool TryBuildPromptRelay(
        WorkflowBridge bridge,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageContext stageContext,
        INodeOutput clipOutput,
        string globalPrompt,
        WGNodeData sourceMedia,
        out string overridePositive)
    {
        overridePositive = null;
        PromptRelayPlan promptRelay = stageContext?.Stage?.RequireLtx2Payload().PromptRelay;
        if (promptRelay is null || promptRelay.Mode == PromptRelayMode.None)
        {
            return false;
        }

        int fps = LtxStageRuntimeSettings.ResolveFps(g, genInfo, sourceMedia);
        int frameCount = genInfo.Frames
            ?? sourceMedia?.Frames
            ?? LtxStageRuntimeSettings.DefaultFrameCount;
        IReadOnlyList<PromptRelaySegmentPlan> segments = promptRelay.Segments;
        PromptRelayMode mode = promptRelay.Mode;
        int incomingHandleFrames = stageContext.ClipContext.IncomingContinueHandleFrames;
        if (incomingHandleFrames > 0)
        {
            double handleSeconds = incomingHandleFrames / (double)Math.Max(1, fps);
            PromptWindowPlan[] shifted = [.. promptRelay.AuthoredWindows.Select(window =>
                window with
                {
                    StartSeconds = window.StartSeconds + handleSeconds,
                    EndSeconds = window.EndSeconds + handleSeconds,
                })];
            double clipSeconds = frameCount / (double)Math.Max(1, fps);
            segments = PromptRelayPlanCompiler.Tile(shifted, clipSeconds);
            mode = PromptRelayPlanCompiler.ModeFor(segments);
        }
        else if (promptRelay.Mode == PromptRelayMode.RequiresRuntimeLength)
        {
            double clipSeconds = frameCount / (double)Math.Max(1, fps);
            segments = PromptRelayPlanCompiler.Tile(promptRelay.AuthoredWindows, clipSeconds);
            mode = PromptRelayPlanCompiler.ModeFor(segments);
        }

        if (mode == PromptRelayMode.SinglePromptOverride
            && segments.Count == 1
            && !string.IsNullOrWhiteSpace(segments[0].Prompt))
        {
            overridePositive = segments[0].Prompt;
            return false;
        }
        if (mode != PromptRelayMode.Relay
            || segments.Count < 2
            || genInfo.Model?.Path is not JArray modelPath)
        {
            return false;
        }

        // Rounded seconds can miss one frame under LTX's (n-1)/grid+1 mapping, so pass the planned
        // latent-frame count.
        JObject relayPayload = new()
        {
            ["latentFrames"] = Ltx2ArchitectureModule.LatentFrameCount(frameCount),
            ["windows"] = new JArray(segments.Select(window => new JObject
            {
                ["prompt"] = window.Prompt ?? "",
                ["seconds"] = Math.Round(window.Seconds, 1),
            })),
        };

        SwarmPromptRelayEncodeNode relay = bridge.AddNode(new SwarmPromptRelayEncodeNode().With(
            GlobalPrompt: globalPrompt ?? "",
            Windows: relayPayload.ToString(Newtonsoft.Json.Formatting.None),
            Fps: fps,
            Epsilon: PromptRelayEpsilon));
        relay.ModelInput.ConnectFromPath(bridge, modelPath);
        relay.Clip.TryConnectToUntyped(clipOutput);

        genInfo.Model = genInfo.Model.WithPath(relay.Model);
        genInfo.PosCond = relay.Positive.ToPath();
        return true;
    }

    private static SwarmClipTextEncodeAdvancedNode AddSwarmClipTextEncodeAdvanced(
        WorkflowBridge bridge,
        INodeOutput clipOutput,
        int steps,
        string prompt,
        int width,
        int height,
        double guidance,
        string nodeId)
    {
        SwarmClipTextEncodeAdvancedNode encode = new SwarmClipTextEncodeAdvancedNode().With(
            Steps: steps,
            Prompt: prompt ?? "",
            Width: width,
            Height: height,
            TargetWidth: width,
            TargetHeight: height,
            Guidance: guidance);
        SwarmClipTextEncodeAdvancedNode node = nodeId is null
            ? bridge.AddNode(encode)
            : bridge.AddNode(encode, nodeId);
        node.Clip.TryConnectToUntyped(clipOutput);
        return node;
    }
}
