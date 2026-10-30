using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Assets_Editor.Tests;

public class ProficienciesTests
{
    // Every optional perk field of the game data: whole and fractional Values, the per-type fields,
    // a type 32 perk without Value (keys only this editor's Extra knows) and an unknown entry key.
    private const string Sample = """
        [
          {"Levels":[{"Perks":[{"Type":0,"Value":1},{"SkillId":11,"Type":25,"Value":0.08}]},
                     {"Perks":[{"AugmentType":17,"SpellId":287,"Type":5,"Value":0.01},
                               {"BestiaryId":12,"BestiaryName":"Lycanthrope","Type":6,"Value":0.03},
                               {"DamageType":1048576,"Type":4,"Value":1},
                               {"ElementId":8,"Type":13,"Value":0.1},
                               {"Range":3,"Type":22,"Value":0.05}]}],
           "Name":"Sample 2H Fist","ProficiencyId":142,"Version":4},
          {"Levels":[{"Perks":[{"ElementId":16,"MissileId":71,"Multiplier":2.0,"Probability":0.01,"Type":32}]}],
           "Name":"Sample Moonsilver","ProficiencyId":475,"Version":7,"Zeta":"kept"}
        ]
        """;

    private static string WriteGameFile(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"proficiencies-{System.Guid.NewGuid():N}.json");
        File.WriteAllText(path, JToken.Parse(json).ToString(Formatting.Indented));
        return path;
    }

    [Fact]
    public void Unedited_file_saves_back_byte_identical()
    {
        string source = WriteGameFile(Sample);
        string copy = source + ".out";
        try
        {
            Assert.True(Proficiencies.Load(source));
            Assert.True(Proficiencies.Save(copy));
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(copy));
        }
        finally
        {
            File.Delete(source);
            File.Delete(copy);
        }
    }

    [Fact]
    public void Edited_value_is_written_and_the_rest_is_kept()
    {
        string source = WriteGameFile(Sample);
        string copy = source + ".out";
        try
        {
            Assert.True(Proficiencies.Load(source));
            Proficiencies.FindById(142)!.Levels[1].Perks[3].ElementId = 64;
            Assert.True(Proficiencies.Save(copy));

            var perks = JArray.Parse(File.ReadAllText(copy))[0]!["Levels"]![1]!["Perks"]!;
            Assert.Equal(64, (int)perks[3]!["ElementId"]!);
            Assert.Null(perks[3]!["DamageType"]);
            Assert.Equal("Lycanthrope", (string?)perks[1]!["BestiaryName"]);
        }
        finally
        {
            File.Delete(source);
            File.Delete(copy);
        }
    }

    [Theory]
    [InlineData(4, ProficiencyEnums.PerkField.Damage)]
    [InlineData(6, ProficiencyEnums.PerkField.Bestiary)]
    [InlineData(9, ProficiencyEnums.PerkField.Element)]
    [InlineData(13, ProficiencyEnums.PerkField.Element)]
    [InlineData(31, ProficiencyEnums.PerkField.Element)]
    [InlineData(32, ProficiencyEnums.PerkField.Element | ProficiencyEnums.PerkField.Missile)]
    public void Perk_types_edit_the_fields_the_game_data_uses(int type, ProficiencyEnums.PerkField fields)
    {
        Assert.Equal(fields, ProficiencyEnums.FieldsFor(type));
    }

    [Fact]
    public void Crystal_server_warning_names_type_32_entries_only()
    {
        string source = WriteGameFile(Sample);
        try
        {
            Assert.True(Proficiencies.Load(source));
            string? warning = Proficiencies.CrystalServerWarning();
            Assert.NotNull(warning);
            Assert.Contains("475", warning);
            Assert.DoesNotContain("142", warning);

            Proficiencies.Entries.RemoveAll(e => e.ProficiencyId == 475);
            Assert.Null(Proficiencies.CrystalServerWarning());
        }
        finally
        {
            File.Delete(source);
        }
    }
}
