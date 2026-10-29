using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;
using static Assets_Editor.OTB;

namespace Assets_Editor.Tests;

// items.otb and items.xml as a TFS 1.x server (Project Wolf) reads them.
public class ServerFormatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tae-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static List<ServerItem> SampleOtbItems() => new()
    {
        new ServerItem { ServerId = 100, ClientId = 100, Type = ServerItemType.Ground, GroundSpeed = 150, Movable = false },
        new ServerItem { ServerId = 101, ClientId = 101, Type = ServerItemType.Container, Pickupable = true },
        // a door: the editor has no type for group 13, it must survive as the raw group byte
        new ServerItem { ServerId = 102, ClientId = 102, Type = ServerItemType.None, RawGroup = 13, Unpassable = true },
        new ServerItem { ServerId = 103, ClientId = 103, Type = ServerItemType.None, Name = "lamp", LightLevel = 7, LightColor = 215 },
    };

    private string WriteOtb(List<ServerItem> items, string name, uint clientVersion = 860)
    {
        string path = Path.Combine(_dir, name);
        var writer = new OTBWriter(items) { MajorVersion = 3, MinorVersion = 20, BuildNumber = 1, ClientVersion = clientVersion };
        Assert.True(writer.Write(path), writer.LastError);
        return path;
    }

    [Fact]
    public void Otb_round_trip_is_byte_identical_and_keeps_unknown_groups_and_version_label()
    {
        string first = WriteOtb(SampleOtbItems(), "a.otb");

        var reader = new OTBReader();
        Assert.True(reader.Read(first));
        Assert.Equal(860u, reader.ClientVersion);
        Assert.Equal(13, reader.Items.Single(i => i.ServerId == 102).RawGroup);

        string second = WriteOtb(reader.Items, "b.otb", reader.ClientVersion);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        Assert.False(File.Exists(second + ".tmp"));
    }

    private const string SampleXml = """
        <?xml version="1.0" encoding="iso-8859-1"?>
        <items>
        	<!-- walls -->
        	<item fromid="373" toid="376" article="a" name="stone wall" />
        	<item id="2120" article="a" name="fire">
        		<attribute key="type" value="magicfield" />
        		<attribute key="decayTo" value="0" />
        		<attribute key="field" value="fire">
        			<attribute key="damage" value="20" />
        		</attribute>
        	</item>
        	<item id="2121" name="coin">
        		<attribute key="showcount" value="0" />
        	</item>
        </items>
        """;

    private string WriteXml()
    {
        string path = Path.Combine(_dir, "items.xml");
        File.WriteAllText(path, SampleXml.Replace("\r\n", "\n"), System.Text.Encoding.Latin1);
        return path;
    }

    private static string Canonical(string path) =>
        string.Join("\n", XDocument.Load(path).Root!.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting)));

    [Fact]
    public void ItemsXml_unchanged_save_keeps_everything_the_engine_reads()
    {
        string path = WriteXml();
        Assert.True(ItemsXml.Load(path));
        Assert.Equal(6, ItemsXml.Items.Count);

        string output = Path.Combine(_dir, "out.xml");
        Assert.True(ItemsXml.Save(output));

        Assert.Equal(Canonical(path), Canonical(output));
        string text = File.ReadAllText(output, System.Text.Encoding.Latin1);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>", text);
        Assert.Contains("<!-- walls -->", text);
    }

    [Fact]
    public void ItemsXml_editing_one_id_of_a_range_splits_only_that_range()
    {
        string path = WriteXml();
        ItemsXml.Load(path);
        ItemsXml.Items[375].Name = "cracked wall";
        ItemsXml.Items[2120].Description = "hot";

        string output = Path.Combine(_dir, "out.xml");
        Assert.True(ItemsXml.Save(output));

        var items = XDocument.Load(output).Root!.Elements("item").ToList();
        Assert.Contains(items, e => (string?)e.Attribute("fromid") == "373" && (string?)e.Attribute("toid") == "374");
        Assert.Contains(items, e => (string?)e.Attribute("id") == "375" && (string?)e.Attribute("name") == "cracked wall");
        Assert.Contains(items, e => (string?)e.Attribute("id") == "376" && (string?)e.Attribute("name") == "stone wall");

        var fire = items.Single(e => (string?)e.Attribute("id") == "2120");
        Assert.Equal("0", fire.Elements("attribute").Single(a => (string?)a.Attribute("key") == "decayTo").Attribute("value")!.Value);
        Assert.Single(fire.Elements("attribute").Single(a => (string?)a.Attribute("key") == "field").Elements());
        Assert.Equal("hot", fire.Elements("attribute").Single(a => (string?)a.Attribute("key") == "description").Attribute("value")!.Value);

        var coin = items.Single(e => (string?)e.Attribute("id") == "2121");
        Assert.Equal("0", coin.Elements("attribute").Single().Attribute("value")!.Value);
    }
}
