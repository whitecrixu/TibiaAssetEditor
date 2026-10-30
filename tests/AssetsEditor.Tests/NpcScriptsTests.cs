using System.IO;
using System.Linq;
using Xunit;

namespace Assets_Editor.Tests;

public class NpcScriptsTests
{
    // The shapes found in TFS 1.5 revscript NPCs: a shop as an NpcSystem string, keyword answers through
    // addKeyword and a local say() helper, an answer put together with "..", a commented-out keyword.
    private const string StringShopNpc = """
        local keywordHandler = KeywordHandler:new()
        local npcHandler = NpcHandler:new(keywordHandler)
        keywordHandler:addKeyword({ "job" }, CrystalModule.say, { npcHandler = npcHandler, text = "I sell {runes}." })
        keywordHandler:addKeyword({'ice'}, StdModule.say, {npcHandler = npcHandler, text = 'I don\'t go there.'})
        keywordHandler:addKeyword({ "time" }, CrystalModule.say, { npcHandler = npcHandler, text = "It is " .. getTime() })
        -- keywordHandler:addKeyword({ "old" }, CrystalModule.say, { npcHandler = npcHandler, text = "unused" })
        npcHandler:setMessage(MESSAGE_GREET, "Hello |PLAYERNAME|.")
        npcHandler:setMessage(MESSAGE_FAREWELL, defaultFarewell)

        local npcType = NpcType("Runa")
        npcType:walkInterval(2000)
        npcType:walkRadius(2)
        npcType:health(100, 100)
        npcType:outfit({lookType = 130, lookHead = 96, lookBody = 71, lookLegs = 63, lookFeet = 97, lookAddons = 0})
        npcType:parameters({
        	["module_shop"] = "1",
        	["shop_buyable"] = "blank rune,3147,10;life ring,3052,900;exercise rod,28556,347222,500",
        	["shop_sellable"] = "life crystal,3061,85",
        })
        npcType:register()
        """;

    // The Rookgaard style: shop rows in Lua tables passed through a helper.
    private const string TableShopNpc = """
        local SELLS = { -- the player buys these
        	{"axe", 3274, 20},
        	{"dagger", 3267, 5},
        }
        local BUYS = {
        	{"axe", 3274, 7},
        }
        local npcType = NpcType("Obo")
        npcType:outfit({lookType = 128, lookHead = 97, lookBody = 58, lookLegs = 38, lookFeet = 95, lookAddons = 0})
        npcType:parameters({
        	["module_shop"] = "1",
        	["shop_buyable"] = shopList(SELLS),
        	["shop_sellable"] = shopList(BUYS),
        })
        """;

    [Fact]
    public void Reads_the_editable_values()
    {
        var npc = new NpcScript("runa.lua", StringShopNpc);
        Assert.Equal("Runa", npc.Name);
        Assert.Equal(130, npc.OutfitValue("lookType"));
        Assert.Equal(2000, npc.WalkInterval!.IntValue);
        Assert.Equal(3, npc.Buyable!.Items.Count);
        Assert.Equal("500", npc.Buyable.Items[2].Extra);
        Assert.Equal(85, npc.Sellable!.Items[0].Price);
        // the concatenated answer, the commented-out one and the variable message are not editable
        Assert.Equal(new[] { "I sell {runes}.", "I don't go there." }, npc.Dialogs.Select(d => d.Token.StringValue));
        Assert.Equal("Hello |PLAYERNAME|.", Assert.Single(npc.Messages).Token.StringValue);
        Assert.False(npc.Modified);
        Assert.Equal(StringShopNpc, npc.BuildText());
    }

    [Fact]
    public void Edits_change_only_their_own_text()
    {
        var npc = new NpcScript("runa.lua", StringShopNpc);
        npc.Buyable!.Items[1].Price = 950;
        npc.Sellable!.Items.Add(new ShopItem { Name = "mind stone", ItemId = 3062, Price = 170 });
        npc.Outfit.First(t => t.Key == "lookHead").IntValue = 0;
        npc.Dialogs[1].Token.StringValue = "I don't sail to \"Ice\".";

        string expected = StringShopNpc
            .Replace("life ring,3052,900", "life ring,3052,950")
            .Replace("\"life crystal,3061,85\"", "\"life crystal,3061,85;mind stone,3062,170\"")
            .Replace("lookHead = 96", "lookHead = 0")
            .Replace(@"'I don\'t go there.'", @"'I don\'t sail to ""Ice"".'");
        Assert.True(npc.Modified);
        Assert.Equal(expected, npc.BuildText());
    }

    [Fact]
    public void Table_shops_keep_their_header_and_layout()
    {
        var npc = new NpcScript("obo.lua", TableShopNpc);
        Assert.Equal("SELLS", npc.Buyable!.TableName);
        Assert.Equal(new[] { "axe", "dagger" }, npc.Buyable.Items.Select(i => i.Name));
        Assert.Equal(TableShopNpc, npc.BuildText());

        npc.Buyable.Items.RemoveAt(1);
        npc.Buyable.Items.Add(new ShopItem { Name = "rapier", ItemId = 3272, Price = 15 });
        Assert.Equal(TableShopNpc.Replace("\t{\"dagger\", 3267, 5},", "\t{\"rapier\", 3272, 15},"), npc.BuildText());
    }

    [Fact]
    public void Save_keeps_a_backup_and_refuses_a_file_changed_meanwhile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "npc-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "runa.lua");
            File.WriteAllText(file, StringShopNpc, NpcScript.FileEncoding);
            var npc = NpcScript.Load(file);
            npc.HealthNow!.IntValue = 150;
            npc.Save();
            Assert.Equal(StringShopNpc, File.ReadAllText(file + "-bak", NpcScript.FileEncoding));
            Assert.Contains("npcType:health(150, 100)", File.ReadAllText(file, NpcScript.FileEncoding));
            Assert.False(npc.Modified);

            npc.HealthNow!.IntValue = 200;
            File.AppendAllText(file, "-- edited elsewhere\n", NpcScript.FileEncoding);
            Assert.Throws<IOException>(npc.Save);

            // the "-bak" copies are not loaded as NPCs
            Assert.Equal(1, NpcRegistry.LoadFolder(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Shop_check_finds_free_gold_and_wrong_names()
    {
        var runa = new NpcScript("runa.lua", StringShopNpc);
        var obo = new NpcScript("obo.lua", TableShopNpc.Replace("{\"axe\", 3274, 7}", "{\"axe\", 3274, 25}"));
        var findings = NpcRegistry.CheckShops(new[] { runa, obo }, id => id != 28556, id => id == 3147 ? "blank rune" : id == 3052 ? "ring of life" : null);

        var free = Assert.Single(findings, f => f.Category == "Free gold");
        Assert.Equal(3274, free.ItemId);
        Assert.Contains("+5 each", free.Details);
        Assert.Contains(findings, f => f.Category == "Unknown item id" && f.ItemId == 28556);
        Assert.Contains(findings, f => f.Category == "Name differs from items.xml" && f.ItemId == 3052);
        Assert.DoesNotContain(findings, f => f.ItemId == 3147);
    }
}

public class CanaryNpcTests
{
    // Canary / Crystal Server layout: npcConfig table, shop rows with buy and/or sell, a row with only a client id
    // (lower-case keys and no name, both accepted by the engine), voices.
    private const string CanaryNpc = """
        local internalNpcName = "Mira"
        local npcType = Game.createNpcType(internalNpcName)
        local npcConfig = {}

        npcConfig.name = internalNpcName
        npcConfig.health = 100
        npcConfig.maxHealth = npcConfig.health
        npcConfig.walkInterval = 2000
        npcConfig.walkRadius = 2

        npcConfig.outfit = {
        	lookType = 136,
        	lookHead = 78,
        }

        npcConfig.voices = {
        	interval = 15000,
        	chance = 50,
        	{ text = "Fresh bread!" },
        }

        npcConfig.shop = { -- the player buys and sells
        	{ itemName = "bread", clientId = 3600, buy = 4, sell = 1 },
        	-- drinks
        	{ itemname = "mug", clientid = 2880, buy = 5, count = 1 },
        	{ itemid = 238, clientId = 238, buy = 158 },
        }

        npcHandler:setMessage(MESSAGE_GREET, "Welcome, |PLAYERNAME|.")
        npcType:register(npcConfig)
        """;

    [Fact]
    public void Reads_the_canary_layout()
    {
        var npc = new NpcScript("mira.lua", CanaryNpc);
        Assert.Equal(NpcFormat.Canary, npc.Format);
        Assert.Equal("Mira", npc.Name);
        Assert.Equal(136, npc.OutfitValue("lookType"));
        Assert.Equal(100, npc.HealthNow!.IntValue);
        Assert.Null(npc.HealthMax); // an expression, not a number
        Assert.Equal(new[] { "VOICE", "MESSAGE_GREET" }, npc.Messages.Select(m => m.Kind));

        var rows = npc.CanaryShop!.Items;
        Assert.Equal(3, rows.Count);
        Assert.Equal((4, 1), (rows[0].Buy!.Value, rows[0].Sell!.Value));
        Assert.Equal("count = 1", rows[1].Extra);
        Assert.Equal("", rows[2].Name);
        Assert.Equal(new[] { 3600, 2880, 238 }, npc.SoldItems().Select(i => i.ItemId));
        Assert.Equal(new[] { 3600 }, npc.BoughtItems().Select(i => i.ItemId));
        Assert.Equal(CanaryNpc, npc.BuildText());
    }

    [Fact]
    public void Shop_edits_touch_only_their_rows()
    {
        var npc = new NpcScript("mira.lua", CanaryNpc);
        var rows = npc.CanaryShop!.Items;
        rows[1].Buy = 6;                             // keeps its lower-case keys
        rows.RemoveAt(2);                            // its line goes away
        rows.Add(new CanaryShopItem { Name = "cheese", ClientId = 3607, Sell = 2 });
        npc.Messages[0].Token.StringValue = "Bread and mugs!";

        string expected = CanaryNpc
            .Replace("\t{ itemname = \"mug\", clientid = 2880, buy = 5, count = 1 },", "\t{ itemname = \"mug\", clientid = 2880, buy = 6, count = 1 },")
            .Replace("\t{ itemid = 238, clientId = 238, buy = 158 },\n", "\t{ itemName = \"cheese\", clientId = 3607, sell = 2 },\n")
            .Replace("\"Fresh bread!\"", "\"Bread and mugs!\"");
        Assert.Equal(expected, npc.BuildText());
    }

    [Fact]
    public void Rows_sharing_a_line_are_removed_with_their_comma()
    {
        string oneLine = CanaryNpc.Replace("""
            npcConfig.shop = { -- the player buys and sells
            	{ itemName = "bread", clientId = 3600, buy = 4, sell = 1 },
            	-- drinks
            	{ itemname = "mug", clientid = 2880, buy = 5, count = 1 },
            	{ itemid = 238, clientId = 238, buy = 158 },
            }
            """, """npcConfig.shop = { { itemName = "bread", clientId = 3600, buy = 4 }, { itemName = "mug", clientId = 2880, buy = 5 } }""");
        var npc = new NpcScript("mira.lua", oneLine);
        npc.CanaryShop!.Items.RemoveAt(0);
        Assert.Contains("""npcConfig.shop = { { itemName = "mug", clientId = 2880, buy = 5 } }""", npc.BuildText());
    }

    [Fact]
    public void Shops_built_by_code_are_read_only()
    {
        var npc = new NpcScript("loot.lua", CanaryNpc.Replace("npcConfig.shop = {", "npcConfig.shop = LootShopConfig\nlocal unused = {"));
        Assert.Null(npc.CanaryShop);
        Assert.NotNull(npc.ShopNote);
    }

    [Fact]
    public void Tfs_npc_type_inside_createNpcType_is_not_mistaken_for_the_tfs_layout()
    {
        var npc = new NpcScript("x.lua", "local npcType = Game.createNpcType(\"Lit\")\nnpcConfig.health = 100\n");
        Assert.Equal(NpcFormat.Canary, npc.Format);
        Assert.Equal("Lit", npc.Name);
    }
}
