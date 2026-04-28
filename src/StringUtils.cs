using Newtonsoft.Json.Linq;

namespace VideoStages;

public static class StringUtils
{
    public static string Compact(string rawValue)
    {
        if (rawValue is null)
        {
            return string.Empty;
        }
        return rawValue.Trim().Replace(" ", "");
    }

    public static bool Equals(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static string ClassTypeOf(JObject node) => $"{node["class_type"]}";

    public static bool NodeTypeMatches(JObject node, string classType) =>
        Equals(ClassTypeOf(node), classType);
}
