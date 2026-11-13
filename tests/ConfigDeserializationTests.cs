using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace PepperDash.Essentials.Plugins.Lg.Display.Tests;

public class ConfigDeserializationTests
{
    private static readonly Lazy<Type?> ConfigType = new(() =>
        AssemblyFixture.PluginAssembly.GetType("PepperDash.Essentials.Plugins.Lg.Display.LgDisplayPropertiesConfig"));

    [Fact]
    public void Config_Class_Exists()
    {
        ConfigType.Value.Should().NotBeNull("LgDisplayPropertiesConfig class should exist in the assembly");
    }

    [Fact]
    public void Config_Has_Parameterless_Constructor()
    {
        ConfigType.Value!.GetConstructor(Type.EmptyTypes).Should()
            .NotBeNull("Config class must have a parameterless constructor for JSON deserialization");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("volumeUpperLimit")]
    [InlineData("volumeLowerLimit")]
    [InlineData("pollIntervalMs")]
    [InlineData("coolingTimeMs")]
    [InlineData("warmingTimeMs")]
    [InlineData("udpSocketKey")]
    [InlineData("macAddress")]
    [InlineData("wolBroadcastAddress")]
    [InlineData("wolPort")]
    [InlineData("smallDisplay")]
    [InlineData("overrideWol")]
    [InlineData("keycode")]
    [InlineData("friendlyNames")]
    public void Config_Property_Has_JsonPropertyAttribute(string jsonName)
    {
        HasJsonProperty(ConfigType.Value!, jsonName).Should()
            .BeTrue($"Config should have a property with [JsonProperty(\"{jsonName}\")]");
    }

    [Theory]
    [InlineData("inputKey")]
    [InlineData("name")]
    [InlineData("hideInput")]
    public void FriendlyName_Property_Has_JsonPropertyAttribute(string jsonName)
    {
        var type = AssemblyFixture.PluginAssembly.GetType("PepperDash.Essentials.Plugins.Lg.Display.FriendlyName");
        type.Should().NotBeNull("FriendlyName class should exist in the assembly");
        HasJsonProperty(type!, jsonName).Should()
            .BeTrue($"FriendlyName should have a property with [JsonProperty(\"{jsonName}\")]");
    }

    [Theory]
    [InlineData("Id", "String")]
    [InlineData("SmallDisplay", "Boolean")]
    [InlineData("OverrideWol", "Boolean")]
    [InlineData("WolBroadcastAddress", "String")]
    [InlineData("WolPort", "Nullable`1")]
    [InlineData("Keycode", "String")]
    public void Config_Property_Type_Matches(string propertyName, string expectedTypeName)
    {
        var prop = ConfigType.Value!.GetProperty(propertyName);
        prop.Should().NotBeNull($"LgDisplayPropertiesConfig should expose {propertyName}");
        prop!.PropertyType.Name.Should().Be(expectedTypeName);
    }

    [Fact]
    public void FriendlyNames_Is_List_Of_FriendlyName()
    {
        var prop = ConfigType.Value!.GetProperty("FriendlyNames");
        prop.Should().NotBeNull("LgDisplayPropertiesConfig should expose FriendlyNames");

        var type = prop!.PropertyType;
        type.IsGenericType.Should().BeTrue("FriendlyNames must be a generic collection");
        type.GetGenericTypeDefinition().Name.Should().Be("List`1");
        type.GetGenericArguments()[0].Name.Should().Be("FriendlyName");
    }

    private const string SampleJson = """
        {
            "id": "1",
            "volumeUpperLimit": 100,
            "volumeLowerLimit": 0,
            "pollIntervalMs": 30000,
            "coolingTimeMs": 15000,
            "warmingTimeMs": 15000,
            "udpSocketKey": "udpKey",
            "macAddress": "00:11:22:33:44:55",
            "wolBroadcastAddress": "192.0.2.255",
            "wolPort": 9,
            "smallDisplay": false,
            "overrideWol": false,
            "friendlyNames": [ { "inputKey": "hdmi1", "name": "Laptop", "hideInput": false } ]
        }
        """;

    [Fact]
    public void Config_Sample_Json_Has_Expected_Keys()
    {
        var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(SampleJson);
        dict.Should().ContainKey("id");
        dict.Should().ContainKey("macAddress");
        dict.Should().ContainKey("wolBroadcastAddress");
        dict.Should().ContainKey("friendlyNames");
    }

    [Fact]
    public void FriendlyNames_Deserialize_As_List_With_Expected_Shape()
    {
        var jo = JObject.Parse(SampleJson);
        var friendlyNames = jo["friendlyNames"] as JArray;

        friendlyNames.Should().NotBeNull("friendlyNames should deserialize as a JSON array");
        friendlyNames!.Should().HaveCount(1);
        friendlyNames![0]["inputKey"]!.Value<string>().Should().Be("hdmi1");
        friendlyNames![0]["name"]!.Value<string>().Should().Be("Laptop");
    }

    private static bool HasJsonProperty(Type type, string jsonName) =>
        type.GetProperties().Any(p =>
            p.CustomAttributes.Any(a =>
                a.AttributeType.Name == "JsonPropertyAttribute"
                && a.ConstructorArguments.Any(arg =>
                    string.Equals(arg.Value?.ToString(), jsonName, StringComparison.Ordinal))));
}
