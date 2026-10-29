using System.Linq;
using Xunit;

namespace Assets_Editor.Tests;

public class KnownClientsTests
{
    [Fact]
    public void Every_known_client_has_a_structure_in_appearances_xml()
    {
        var structures = MainWindow.datStructure.GetAllVersions().Select(v => v.Structure).ToHashSet();
        Assert.All(KnownClients.All, c => Assert.Contains(c.Structure, structures));
    }

    [Fact]
    public void Shared_signatures_never_disagree_on_the_structure()
    {
        Assert.All(KnownClients.All.GroupBy(c => c.DatSignature), g => Assert.Single(g.Select(c => c.Structure).Distinct()));
    }

    [Theory]
    [InlineData(0x4C2C7993u, "8.60 v2", 5)]   // NewEra / WolfOTS client
    [InlineData(0x4C28B721u, "8.60 v1", 5)]
    [InlineData(0x467FD7E6u, "8.00", 4)]
    [InlineData(0x439D5A33u, "7.60 / 7.70", 3)]
    [InlineData(0x000042A3u, "10.98", 6)]
    public void Signatures_resolve_to_the_client_and_structure(uint signature, string name, int structure)
    {
        var client = KnownClients.FromDat(signature);
        Assert.NotNull(client);
        Assert.Equal(name, client!.Name);
        Assert.Equal(structure, client.Structure);
    }

    [Fact]
    public void Unknown_signature_is_null() => Assert.Null(KnownClients.FromDat(0x524C4449));
}
