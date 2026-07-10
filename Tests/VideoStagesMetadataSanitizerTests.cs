using Newtonsoft.Json.Linq;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MetadataSanitizerTests
{
    [Fact]
    public void StripUploadData_RemovesEmbeddedPayloads_KeepsFileNames()
    {
        string raw = new JObject
        {
            ["Clips"] = new JArray
            {
                new JObject
                {
                    ["UploadedAudio"] = new JObject
                    {
                        ["Data"] = "data:audio/wav;base64,QUJD",
                        ["FileName"] = "a.wav"
                    },
                    ["Refs"] = new JArray
                    {
                        new JObject
                        {
                            ["UploadedImage"] = new JObject
                            {
                                ["Data"] = "data:image/png;base64,QUJD",
                                ["FileName"] = "r.png"
                            }
                        }
                    }
                }
            }
        }.ToString();
        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject root = JObject.Parse(sanitized);
        JObject clip = (JObject)root["Clips"]![0]!;
        Assert.Null(clip["UploadedAudio"]!["Data"]);
        Assert.Equal("a.wav", $"{clip["UploadedAudio"]!["FileName"]}");
        JObject ref0 = (JObject)clip["Refs"]![0]!;
        Assert.Null(ref0["UploadedImage"]!["Data"]);
        Assert.Equal("r.png", $"{ref0["UploadedImage"]!["FileName"]}");
    }

    [Fact]
    public void StripUploadData_RemovesUploadContainerWhenOnlyPayloadWasPresent()
    {
        string raw = new JObject
        {
            ["Clips"] = new JArray
            {
                new JObject
                {
                    ["UploadedAudio"] = new JObject
                    {
                        ["Data"] = "data:audio/wav;base64,QUJD"
                    }
                }
            }
        }.ToString();
        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject clip = (JObject)JObject.Parse(sanitized)["Clips"]![0]!;
        Assert.Null(clip["UploadedAudio"]);
    }

    [Fact]
    public void StripUploadData_BareClipArrayRoot_StripsPayloads()
    {
        // This branch's Data JSON may be a bare clip array (no top-level object).
        string raw = new JArray
        {
            new JObject
            {
                ["UploadedAudio"] = new JObject
                {
                    ["Data"] = "data:audio/wav;base64,QUJD",
                    ["FileName"] = "a.wav"
                }
            }
        }.ToString();
        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject clip = (JObject)JArray.Parse(sanitized)[0]!;
        Assert.Null(clip["UploadedAudio"]!["Data"]);
        Assert.Equal("a.wav", $"{clip["UploadedAudio"]!["FileName"]}");
    }

    [Fact]
    public void StripUploadData_InvalidJson_ReturnsOriginal()
    {
        string raw = "{not json";
        Assert.Equal(raw, MetadataSanitizer.StripUploadDataFromJsonParameter(raw));
    }
}
