using Epi.Display.Lg;
using FluentAssertions;
using Xunit;

namespace Epi.Display.Lg.Tests;

/// <summary>
/// The model a webOS display publishes over UPnP. The shapes are an LG UR340C's, captured 2026-11-18: two descriptions,
/// one with a generic model name and the model number, one with the model as its name. Addresses are made up.
/// </summary>
public class LgUpnpDescriptionTests
{
    private const string Host = "192.0.2.53";

    private const string Basic = """
        <?xml version="1.0"?><root xmlns="urn:schemas-upnp-org:device-1-0"><device>
        <deviceType>urn:schemas-upnp-org:device:Basic:1</deviceType><friendlyName>[LG] webOS TV UR340C9UD</friendlyName>
        <manufacturer>LG Electronics</manufacturer><modelName>LG Smart TV</modelName><modelNumber>55UR340C9UD</modelNumber>
        <serialNumber></serialNumber><UDN>uuid:00000000-0000-0000-0000-000000000001</UDN></device></root>
        """;

    private const string Tv = """
        <root xmlns="urn:schemas-upnp-org:device-1-0"><device><deviceType>urn:lge:device:tv:1</deviceType>
        <manufacturer>LG Electronics</manufacturer><modelName>55UR340C9UD</modelName><modelDescription></modelDescription>
        <serialNumber></serialNumber></device></root>
        """;

    [Fact]
    public void The_model_number_is_the_model()
    {
        LgUpnpDescription.Model(Basic).Should().Be("55UR340C9UD");
    }

    [Fact]
    public void A_model_name_that_is_a_model_is_taken_when_there_is_no_number()
    {
        LgUpnpDescription.Model(Tv).Should().Be("55UR340C9UD");
    }

    [Theory]
    [InlineData("<root><device><modelName>LG Smart TV</modelName><modelNumber></modelNumber></device></root>")]
    [InlineData("<root><device><friendlyName>TV</friendlyName></device></root>")]
    [InlineData("")]
    [InlineData(null)]
    public void A_description_without_a_model_gives_none(string? description)
    {
        LgUpnpDescription.Model(description!).Should().BeNull();
    }

    [Fact]
    public void Only_the_display_s_own_http_descriptions_are_read()
    {
        var replies = new[]
        {
            "HTTP/1.1 200 OK\r\nLOCATION: http://192.0.2.53:1584/\r\nSERVER: WebOS/4.0.0 UPnP/1.0\r\n\r\n",
            "HTTP/1.1 200 OK\r\nlocation: http://192.0.2.53:1819/\r\n\r\n",
            "HTTP/1.1 200 OK\r\nLOCATION: http://192.0.2.53:1584/\r\n\r\n",
            "HTTP/1.1 200 OK\r\nLOCATION: http://192.0.2.99:1400/\r\n\r\n",
            "HTTP/1.1 200 OK\r\nLOCATION: https://192.0.2.53:1900/\r\n\r\n",
        };

        LgUpnpDescription.Locations(replies, Host).Should().Equal("http://192.0.2.53:1584/", "http://192.0.2.53:1819/");
        LgUpnpDescription.Locations(null!, Host).Should().BeEmpty();
    }

    [Fact]
    public void The_known_description_ports_are_the_fallback()
    {
        LgUpnpDescription.KnownLocations(Host).Should().Equal("http://192.0.2.53:1584/", "http://192.0.2.53:1819/");
        LgUpnpDescription.KnownLocations(" ").Should().BeEmpty();
    }

    [Fact]
    public void The_search_asks_the_display_itself_for_every_service()
    {
        var request = LgUpnpDescription.SearchRequest(Host);

        request.Should().StartWith("M-SEARCH * HTTP/1.1\r\n");
        request.Should().Contain("HOST: 192.0.2.53:1900\r\n");
        request.Should().Contain("ST: ssdp:all\r\n");
        request.Should().EndWith("\r\n\r\n");
    }
}
