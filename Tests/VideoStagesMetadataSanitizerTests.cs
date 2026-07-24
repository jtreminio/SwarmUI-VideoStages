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
            ["clips"] = new JArray
            {
                new JObject
                {
                    ["uploadedAudio"] = new JObject
                    {
                        ["data"] = "data:audio/wav;base64,QUJD",
                        ["fileName"] = "a.wav"
                    },
                    ["refs"] = new JArray
                    {
                        new JObject
                        {
                            ["uploadedImage"] = new JObject
                            {
                                ["data"] = "data:image/png;base64,QUJD",
                                ["fileName"] = "r.png"
                            }
                        }
                    },
                    ["icLoras"] = new JArray
                    {
                        new JObject
                        {
                            ["lora"] = "some-lora",
                            ["driveMedia"] = new JObject
                            {
                                ["data"] = "data:video/mp4;base64,QUJD",
                                ["fileName"] = "drive.mp4"
                            }
                        }
                    }
                }
            }
        }.ToString();
        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject root = JObject.Parse(sanitized);
        JObject clip = (JObject)root["clips"]![0]!;
        Assert.Null(clip["uploadedAudio"]!["data"]);
        Assert.Equal("a.wav", $"{clip["uploadedAudio"]!["fileName"]}");
        JObject ref0 = (JObject)clip["refs"]![0]!;
        Assert.Null(ref0["uploadedImage"]!["data"]);
        Assert.Equal("r.png", $"{ref0["uploadedImage"]!["fileName"]}");
        JObject icLora = (JObject)clip["icLoras"]![0]!;
        Assert.Null(icLora["driveMedia"]!["data"]);
        Assert.Equal("drive.mp4", $"{icLora["driveMedia"]!["fileName"]}");
        Assert.Equal("some-lora", $"{icLora["lora"]}");
    }

    [Fact]
    public void StripUploadData_SourceVideo_KeepsRangeFieldsAndFileName()
    {
        string raw = new JObject
        {
            ["clips"] = new JArray
            {
                new JObject
                {
                    ["sourceVideo"] = new JObject
                    {
                        ["data"] = "data:video/mp4;base64,QUJD",
                        ["fileName"] = "footage.mp4",
                        ["startSeconds"] = 1.5
                    }
                }
            }
        }.ToString();
        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject clip = (JObject)JObject.Parse(sanitized)["clips"]![0]!;
        Assert.Null(clip["sourceVideo"]!["data"]);
        Assert.Equal("footage.mp4", $"{clip["sourceVideo"]!["fileName"]}");
        Assert.Equal(1.5, (double)clip["sourceVideo"]!["startSeconds"]!);
    }

    [Fact]
    public void StripUploadData_RemovesUploadContainerWhenOnlyPayloadWasPresent()
    {
        string raw = new JObject
        {
            ["clips"] = new JArray
            {
                new JObject
                {
                    ["uploadedAudio"] = new JObject
                    {
                        ["data"] = "data:audio/wav;base64,QUJD"
                    }
                }
            }
        }.ToString();
        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject clip = (JObject)JObject.Parse(sanitized)["clips"]![0]!;
        Assert.Null(clip["uploadedAudio"]);
    }

    [Fact]
    public void StripUploadData_BareClipArrayRoot_IsLeftUnchanged()
    {
        // The document envelope is always a versioned root object; a bare clip array is not a
        // Video Stages document, so the sanitizer leaves it alone.
        string raw = new JArray
        {
            new JObject
            {
                ["uploadedAudio"] = new JObject
                {
                    ["data"] = "data:audio/wav;base64,QUJD",
                    ["fileName"] = "a.wav"
                }
            }
        }.ToString();
        Assert.Equal(raw, MetadataSanitizer.StripUploadDataFromJsonParameter(raw));
    }

    [Fact]
    public void StripUploadData_InvalidJson_ReturnsOriginal()
    {
        string raw = "{not json";
        Assert.Equal(raw, MetadataSanitizer.StripUploadDataFromJsonParameter(raw));
    }
}
