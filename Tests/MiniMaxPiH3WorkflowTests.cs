using System.Text;
using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.MiniMax;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class MiniMaxPiH3WorkflowTests : IDisposable
{
    private readonly DirectoryInfo _modelDirectory = Directory.CreateTempSubdirectory("vst-pi-h3-");
    private readonly T2IModelHandler _loras = new() { ModelType = "LoRA" };

    public void Dispose() => _modelDirectory.Delete(recursive: true);

    [Fact]
    public void Managed_installer_is_registered_and_matches_the_frontend()
    {
        Assert.Equal(
            MiniMaxPiH3Adapter.RenderGeneratedTypeScript(),
            RepoFiles.ReadFrontend("generatedPiH3.ts"));
        InstallableFeatures.ComfyInstallableFeature feature = InstallableFeatures.ComfyFeatures["piflow"];
        Assert.Equal("https://github.com/Lakonik/ComfyUI-piFlow", feature.URL);
        Assert.False(feature.AutoInstall);
        Assert.Equal(MiniMaxPiH3Adapter.LoaderFeature,
            ComfyUIBackendExtension.NodeToFeatureMap[LoadLakonLabAdapterNode.ClassType]);
        Assert.Equal(MiniMaxPiH3Adapter.DecodeFeature,
            ComfyUIBackendExtension.NodeToFeatureMap[DWDDecodeNode.ClassType]);
    }

    [Theory]
    [InlineData("quick-pixels.safetensors", 6, "euler", "beta", true)]
    [InlineData("quick-latents", 7, "heun", "normal", false)]
    public async Task Stage_adapter_uses_the_authored_sampler_and_scheduler(
        string adapterFile, int steps, string samplerName, string scheduler, bool dwd)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        string selection = $"minimax-h3/{adapterFile}";
        string adapterName = selection.EndsWith(".safetensors") ? selection : $"{selection}.safetensors";
        InstallLora(adapterName, AdapterHeader(dwd));
        JObject stage = fixture.Stage(steps: steps);
        stage["sampler"] = samplerName;
        stage["scheduler"] = scheduler;
        stage["loras"] = new JArray(new JObject { ["name"] = selection, ["weight"] = 0.8 });

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(MakeClip(stage))),
            dwd ? [MiniMaxPiH3Adapter.LoaderFeature, MiniMaxPiH3Adapter.DecodeFeature]
                : [MiniMaxPiH3Adapter.LoaderFeature]);

        JProperty adapter = Node(LoadLakonLabAdapterNode.ClassType);
        Assert.Equal(adapterName, adapter.Value["inputs"]?.Value<string>("adapter_name"));
        Assert.Equal(0.8, adapter.Value["inputs"]?.Value<double>("adapter_strength"));
        Assert.DoesNotContain(workflow.Properties(), node =>
            node.Value["inputs"]?.Value<string>("lora_name") == adapterName);

        JProperty sampler = Node("SwarmKSampler");
        Assert.Equal(adapter.Name, sampler.Value["inputs"]?["model"]?[0]?.Value<string>());
        Assert.Equal(steps, sampler.Value["inputs"]?.Value<int>("steps"));
        Assert.Equal(samplerName, sampler.Value["inputs"]?.Value<string>("sampler_name"));
        Assert.Equal(scheduler, sampler.Value["inputs"]?.Value<string>("scheduler"));
        Assert.DoesNotContain(workflow.Properties(), node =>
            node.Value.Value<string>("class_type") is "pi-Flow Scheduler" or "SamplerCustom");

        JProperty separated = Node("LTXVSeparateAVLatent");
        Assert.Equal(sampler.Name, separated.Value["inputs"]?["av_latent"]?[0]?.Value<string>());
        Assert.Equal(separated.Name, Node("VAEDecodeAudio").Value["inputs"]?["samples"]?[0]?.Value<string>());
        if (dwd)
        {
            Assert.Equal(sampler.Name, Node(DWDDecodeNode.ClassType).Value["inputs"]?["samples"]?[0]?.Value<string>());
            Assert.DoesNotContain(workflow.Properties(), node =>
                node.Value.Value<string>("class_type") is "VAEDecode" or "VAEDecodeTiled");
        }
        else
        {
            Assert.Equal(separated.Name, Node("VAEDecodeTiled").Value["inputs"]?["samples"]?[0]?.Value<string>());
        }

        JProperty Node(string classType) => Assert.Single(workflow.Properties(),
            node => node.Value.Value<string>("class_type") == classType);
    }

    [Fact]
    public async Task Stage_loads_pi_h3_adapters_in_authored_order()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        InstallLora("pi_h3_2step_adapter.safetensors", AdapterHeader(dwd: false));
        InstallLora("pi_h3_4step_dwd_adapter_int8_convrot.safetensors", AdapterHeader(dwd: true));
        JObject stage = fixture.Stage(steps: 2);
        stage["loras"] = new JArray(
            new JObject { ["name"] = "pi_h3_2step_adapter", ["weight"] = 0.7 },
            new JObject { ["name"] = "pi_h3_4step_dwd_adapter_int8_convrot", ["weight"] = 0.5 });

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(MakeClip(stage))),
            [MiniMaxPiH3Adapter.LoaderFeature, MiniMaxPiH3Adapter.DecodeFeature]);

        JProperty sampler = Node("SwarmKSampler");
        JToken second = workflow[sampler.Value["inputs"]["model"][0].Value<string>()];
        JToken first = workflow[second["inputs"]["model"][0].Value<string>()];
        Assert.Equal(LoadLakonLabAdapterNode.ClassType, first.Value<string>("class_type"));
        Assert.Equal(LoadLakonLabAdapterNode.ClassType, second.Value<string>("class_type"));
        Assert.Equal("pi_h3_2step_adapter.safetensors", first["inputs"].Value<string>("adapter_name"));
        Assert.Equal("pi_h3_4step_dwd_adapter_int8_convrot.safetensors", second["inputs"].Value<string>("adapter_name"));
        Assert.Equal(sampler.Name, Node(DWDDecodeNode.ClassType).Value["inputs"]["samples"][0].Value<string>());

        JProperty Node(string classType) => Assert.Single(workflow.Properties(),
            node => node.Value.Value<string>("class_type") == classType);
    }

    [Fact]
    public async Task A_latent_upscale_without_sampling_does_not_use_its_dwd_adapter()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        const string adapterName = "pi_h3_2step_dwd_adapter_int8_convrot.safetensors";
        InstallLora(adapterName, AdapterHeader(dwd: true));
        JObject upscale = fixture.Stage("PreviousStage", control: 0, upscale: 1.5,
            upscaleMethod: "latent-bislerp", steps: 2);
        upscale["loras"] = new JArray(new JObject
        {
            ["name"] = adapterName,
            ["weight"] = 1,
        });

        JObject workflow = await fixture.GenerateAsync(MakeDocument(MakeClip(fixture.Stage(), upscale)));

        Assert.Contains(workflow.Properties(), node => node.Value.Value<string>("class_type") == "LatentUpscaleBy");
        Assert.DoesNotContain(workflow.Properties(), node =>
            node.Value.Value<string>("class_type") is LoadLakonLabAdapterNode.ClassType or DWDDecodeNode.ClassType
            || node.Value["inputs"]?.Value<string>("lora_name") == adapterName);
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.0, true)]
    [InlineData(1.5, false)]
    public async Task A_second_stage_reuses_dwd_video_latent(
        double upscale, bool publishIntermediate)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        const string adapterName = "quick-pixels.safetensors";
        InstallLora(adapterName, AdapterHeader(dwd: true));
        JObject firstStage = fixture.Stage(steps: 4);
        JObject secondStage = fixture.Stage(
            "PreviousStage", control: 0.5, upscale: upscale, upscaleMethod: "latent-bislerp", steps: 4);
        foreach (JObject stage in new[] { firstStage, secondStage })
        {
            stage["loras"] = new JArray(new JObject { ["name"] = adapterName, ["weight"] = 1 });
        }

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(MakeDocument(MakeClip(firstStage, secondStage)),
                post => post["outputintermediateimages"] = publishIntermediate),
            [MiniMaxPiH3Adapter.LoaderFeature, MiniMaxPiH3Adapter.DecodeFeature]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        LTXVSeparateAVLatentNode firstOutput = OutputOf(bridge, first);
        LTXVConcatAVLatentNode secondInput = JointLatentOf(second);

        if (upscale > 1)
        {
            LatentUpscaleByNode scale = Assert.Single(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
            Assert.Same(firstOutput.VideoLatent, scale.Samples.Connection);
            Assert.Same(scale.LATENT, secondInput.VideoLatent.Connection);
        }
        else
        {
            Assert.Same(firstOutput.VideoLatent, secondInput.VideoLatent.Connection);
        }
        SetLatentNoiseMaskNode audioMask = Assert.IsType<SetLatentNoiseMaskNode>(
            secondInput.AudioLatent.Connection?.Node);
        Assert.Same(firstOutput.AudioLatent, audioMask.Samples.Connection);
        Assert.Empty(bridge.Graph.NodesOfType<IVaeEncode>());

        IReadOnlyList<DWDDecodeNode> decodes = bridge.Graph.NodesOfType<DWDDecodeNode>();
        Assert.Equal(publishIntermediate ? 2 : 1, decodes.Count);
        Assert.Contains(decodes, decode => ReferenceEquals(second, decode.Samples.Connection?.Node));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_pi_h3_nodes_point_to_the_settings_installer(bool dwd)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        const string adapterName = "adapter.safetensors";
        InstallLora(adapterName, AdapterHeader(dwd));
        JObject stage = fixture.Stage();
        stage["loras"] = new JArray(new JObject { ["name"] = adapterName, ["weight"] = 1 });

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.Post(MakeDocument(MakeClip(stage))),
                dwd ? [MiniMaxPiH3Adapter.LoaderFeature] : []));

        Assert.Contains("Install it from VideoStages settings", error.Message);
    }

    [Fact]
    public async Task A_normal_lora_with_a_pi_h3_filename_still_uses_the_normal_loader()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        const string name = "pi_h3_2step_adapter.safetensors";
        InstallLora(name, new JObject { ["transformer_blocks.0.attn.to_q.lora_A.weight"] = new JObject() });
        JObject stage = fixture.Stage();
        stage["loras"] = new JArray(new JObject { ["name"] = name, ["weight"] = 1 });

        JObject workflow = await fixture.GenerateAsync(MakeDocument(MakeClip(stage)));

        Assert.Contains(workflow.Properties(), node => node.Value["inputs"]?.Value<string>("lora_name") == name);
        Assert.DoesNotContain(workflow.Properties(), node =>
            node.Value.Value<string>("class_type") is LoadLakonLabAdapterNode.ClassType or DWDDecodeNode.ClassType);
    }

    private void InstallLora(string name, JObject header)
    {
        string path = Path.Join(_modelDirectory.FullName, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        byte[] headerBytes = Encoding.UTF8.GetBytes(header.ToString(Newtonsoft.Json.Formatting.None));
        File.WriteAllBytes(path, [.. BitConverter.GetBytes((long)headerBytes.Length), .. headerBytes]);
        _loras.Models[name] = new T2IModel(_loras, _modelDirectory.FullName, path, name);
        Program.T2IModelSets["LoRA"] = _loras;
    }

    private static JObject AdapterHeader(bool dwd)
    {
        JObject header = new() { ["__metadata__"] = new JObject { ["policy_config"] = "{}" } };
        if (dwd)
        {
            header["final_layer.audio_out.weight"] = new JObject();
            header["final_layer.video_out.weight"] = new JObject();
            header["dwd_pixel_refiner.input_proj.weight"] = new JObject();
        }
        else
        {
            header["audio_proj_out.weight"] = new JObject();
            header["proj_out.weight"] = new JObject();
        }
        return header;
    }
}
