using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Assets_Editor.Tests;

// TFS revscript monsters (mType:xxx(...) calls) are patched in place, never regenerated.
public class MonsterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tae-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private const string Dragon = "-- Converted from Canary dragons/dragon.lua\n" +
        "local mType = Game.createMonsterType(\"Dragon\")\n" +
        "mType:name(\"Dragon\")\n" +
        "mType:experience(700)\n" +
        "mType:outfit({lookType = 34})\n" +
        "mType:maxHealth(1000)\n" +
        "mType:health(1000)\n" +
        "mType:race(\"blood\")\n" +
        "mType:corpseId(5973)\n" +
        "\n" +
        "do\n" +
        "\tlocal loot = Loot()\n" +
        "\tloot:setId(2148) -- gold coin\n" +
        "\tloot:setChance(100000)\n" +
        "\tloot:setMaxCount(80)\n" +
        "\tmType:addLoot(loot)\n" +
        "end\n" +
        "\n" +
        "-- keep me\n" +
        "mType:register()\n";

    private string WriteDragon()
    {
        string path = Path.Combine(_dir, "dragon.lua");
        File.WriteAllText(path, Dragon);
        return path;
    }

    [Fact]
    public void Unchanged_monster_is_not_rewritten()
    {
        string path = WriteDragon();
        var d = LuaMonsterParser.ParseFile(path)!;
        Assert.False(d.ReadOnly);
        Assert.Equal(700u, d.Experience);
        Assert.Single(d.Loot);

        Assert.True(LuaMonsterGenerator.SaveToFile(d));
        Assert.Equal(Dragon, File.ReadAllText(path));
        Assert.False(File.Exists(path + "-bak"));
    }

    [Fact]
    public void Edit_changes_only_the_edited_lines()
    {
        string path = WriteDragon();
        var d = LuaMonsterParser.ParseFile(path)!;
        d.Experience = 900;
        d.Loot[0].Chance = 50000;

        Assert.True(LuaMonsterGenerator.SaveToFile(d));
        string text = File.ReadAllText(path);
        Assert.Equal(Dragon.Replace("mType:experience(700)", "mType:experience(900)")
                           .Replace("loot:setChance(100000)", "loot:setChance(50000)"), text);
        Assert.Equal(Dragon, File.ReadAllText(path + "-bak"));
    }
}
