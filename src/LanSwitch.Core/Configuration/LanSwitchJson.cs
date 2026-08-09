using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanSwitch.Core.Configuration;

public static class LanSwitchJson
{
    public static JsonSerializerOptions CreateOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = writeIndented
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
