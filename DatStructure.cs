using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tibia.Protobuf.Appearances;
using Appearance = Tibia.Protobuf.Appearances.Appearance;

namespace Assets_Editor
{
    public class FlagInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Version { get; set; }
    }
    public class DatInfo
    {
        public uint Signature { get; set; }
        public ushort ObjectCount { get; set; }
        public ushort OutfitCount { get; set; }
        public ushort EffectCount { get; set; }
        public ushort MissileCount { get; set; }

    }

    public class VersionInfo
    {
        public string? Name { get; set; }
        public int Structure { get; set; }
        public bool UsePatternZ { get; set; }
        public bool UseRDBytes { get; set; }
        public Dictionary<string, FlagInfo> FlagsByName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, FlagInfo> FlagsById { get; set; } = [];
        public bool HasFlag(string flagName)
        {
            return FlagsByName.ContainsKey(flagName);
        }
        public FlagInfo? GetFlagInfo(string flagName)
        {
            if (FlagsByName.TryGetValue(flagName, out var flagInfo))
                return flagInfo;
            return null;
        }
        public int GetFlagId(string flagName)
        {
            if (string.IsNullOrWhiteSpace(flagName))
                throw new ArgumentException("Flag name cannot be null or empty.", nameof(flagName));

            if (FlagsByName.TryGetValue(flagName, out var flagInfo))
                return flagInfo.Id;

            throw new KeyNotFoundException($"Flag '{flagName}' not found in version {Name}.");
        }

        public override string ToString() {
            return $"{Name}";
        }
    }

    public class DatStructure
    {
        private Dictionary<int, VersionInfo> versions = [];

        public DatStructure()
        {
            string xmlPath = "Appearances.xml";
            if (!File.Exists(xmlPath)) {
                throw new FileNotFoundException($"Appearances XML not found: {xmlPath}");
            }

            XDocument doc;
            try
            {
                doc = XDocument.Load(xmlPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load XML file '{xmlPath}'.", ex);
            }


            var appearancesElement = doc.Root;
            string? obdStructure = appearancesElement?.Attribute("obd_structure")?.Value;
            if (obdStructure == null || !int.TryParse(obdStructure, out int obdStructureInt)) {
                throw new InvalidOperationException($"Failed to load XML file '{xmlPath}'. OBD structure not defined!");
            }

            foreach (var versionElement in doc.Descendants("Dat"))
            {
                var structureVersion = versionElement.Attribute("structure");
                var structureName = versionElement.Attribute("name");
                var usePatternZ = versionElement.Attribute("pattern_z");
                var useRDBytes = versionElement.Attribute("rdbytes");

                // 7.0-7.5 did not use patternZ
                bool usePatternZValue = true;
                if (usePatternZ != null && usePatternZ.Value.Equals("false", StringComparison.OrdinalIgnoreCase)) {
                    usePatternZValue = false;
                }

                bool useRDBytesValue = false;
                if (useRDBytes != null && useRDBytes.Value.Equals("true", StringComparison.OrdinalIgnoreCase)) {
                    useRDBytesValue = true;
                }

                if (structureName == null) {
                    ErrorManager.ShowWarning($"{xmlPath}: <Dat> is missing a valid 'name' attribute.");
                    continue;
                }

                if (structureVersion == null || !int.TryParse(structureVersion.Value, out int structure)) {
                    ErrorManager.ShowWarning($"{xmlPath}: Version {structureName.Value} is missing a valid 'structure' attribute.");
                    continue;
                }

                VersionInfo versionInfo = new() {
                    Name = structureName.Value,
                    Structure = structure,
                    UsePatternZ = usePatternZValue,
                    UseRDBytes = useRDBytesValue,
                };

                // showing a popup for every flag that is wrong would be annoying
                // let's show it just once
                string flagIssues = "";

                var flagsElement = versionElement.Element("Flags");
                if (flagsElement != null)
                {
                    foreach (var flagElement in flagsElement.Descendants("Flag"))
                    {
                        var idAttr = flagElement.Attribute("id");
                        var nameAttr = flagElement.Attribute("name");
                        var versionAttr = flagElement.Attribute("version");

                        // using int there for two reasons
                        // 1. to support "-1" when the flag is not present
                        // 2. to support u16 attributes in the future
                        if (idAttr == null || !int.TryParse(idAttr.Value, out int id)) {
                            flagIssues += $"Flag under version {structureVersion} is missing a valid 'id' attribute.\n";
                            continue;
                        }

                        if (nameAttr == null) {
                            flagIssues += $"Flag with id {id} under version {structureVersion} is missing the 'name' attribute.\n";
                            continue;
                        }

                        if (versionAttr == null || !int.TryParse(versionAttr.Value, out int version)) {
                            version = 1;
                        }

                        string flagName = nameAttr.Value;
                        var flagInfo = new FlagInfo { Id = id, Name = flagName, Version = version};
                        versionInfo.FlagsByName[flagName] = flagInfo;
                        versionInfo.FlagsById[id] = flagInfo;
                    }
                }

                // show all errors
                if (flagIssues.Length > 0) {
                    ErrorManager.ShowWarning($"{xmlPath}: Version {structureName.Value} has invalid flags:\n\n{flagIssues}");
                }

                versions[structure] = versionInfo;

                // set the default obd encoding standard
                if (versionInfo.Structure == obdStructureInt) {
                    ObdDecoder.SetDatStructure(versionInfo);
                }
            }
        }

        public VersionInfo GetVersionInfo(int versionNumber)
        {
            if (!versions.TryGetValue(versionNumber, out var vInfo))
                throw new InvalidOperationException($"No data found for version {versionNumber}");
            return vInfo;
        }

        public IEnumerable<VersionInfo> GetAllVersions()
        {
            return versions.Values;
        }

        public bool HasFlag(int versionNumber, string flagName)
        {
            var vInfo = GetVersionInfo(versionNumber);
            return vInfo.FlagsByName.ContainsKey(flagName);
        }

        public int GetStructure(int versionNumber)
        {
            var vInfo = GetVersionInfo(versionNumber);
            return vInfo.Structure;
        }

        public static DatInfo ReadAppearanceInfo(BinaryReader r)
        {
            DatInfo info = new()
            {
                Signature = r.ReadUInt32(),
                ObjectCount = r.ReadUInt16(),
                OutfitCount = r.ReadUInt16(),
                EffectCount = r.ReadUInt16(),
                MissileCount = r.ReadUInt16()
            };

            return info;
        }

        public static Appearance ReadAppearance(BinaryReader r, APPEARANCE_TYPE type, VersionInfo versionInfo, PresetSettings preset)
        {
            Appearance appearance = new() {
                AppearanceType = type,
                Flags = new()
            };

            ReadAppearanceAttr(appearance, r, versionInfo);

            bool isExtended = preset.Extended;
            bool hasFrameDurations = preset.FrameDurations;
            bool hasFrameGroups = preset.FrameGroups;

            byte FrameGroupCount = 1;

            // read frame groups if applicable to this version
            if (hasFrameGroups && type == APPEARANCE_TYPE.AppearanceOutfit) {
                FrameGroupCount = r.ReadByte();
            }

            for (int i = 0; i < FrameGroupCount; i++) {
                FrameGroup frameGroup = new() {
                    SpriteInfo = new()
                };

                // read frame groups if applicable to this version
                if (hasFrameGroups && type == APPEARANCE_TYPE.AppearanceOutfit) {
                    byte FrameGroupType = r.ReadByte();
                    frameGroup.FixedFrameGroup = (FIXED_FRAME_GROUP)FrameGroupType;
                } else {
                    frameGroup.FixedFrameGroup = FIXED_FRAME_GROUP.OutfitIdle;
                }

                frameGroup.SpriteInfo.PatternWidth = r.ReadByte();
                frameGroup.SpriteInfo.PatternHeight = r.ReadByte();

                if (frameGroup.SpriteInfo.PatternWidth > 1 || frameGroup.SpriteInfo.PatternHeight > 1) {
                    frameGroup.SpriteInfo.PatternSize = r.ReadByte();
                } else {
                    frameGroup.SpriteInfo.PatternSize = 32;
                }

                frameGroup.SpriteInfo.PatternLayers = r.ReadByte();
                frameGroup.SpriteInfo.PatternX = r.ReadByte();
                frameGroup.SpriteInfo.PatternY = r.ReadByte();

                // old versions did not use pattern Z
                if (versionInfo.UsePatternZ) {
                    frameGroup.SpriteInfo.PatternZ = r.ReadByte();
                } else {
                    frameGroup.SpriteInfo.PatternZ = 1;
                }

                frameGroup.SpriteInfo.PatternFrames = r.ReadByte();

                if (frameGroup.SpriteInfo.PatternFrames > 1) {
                    if (hasFrameDurations) {
                        // frame durations present
                        // read animation mode and durations
                        SpriteAnimation spriteAnimation = new() {
                            AnimationMode = (ANIMATION_ANIMATION_MODE)r.ReadByte(),
                            LoopCount = r.ReadUInt32(),
                            DefaultStartPhase = r.ReadByte()
                        };

                        for (int k = 0; k < frameGroup.SpriteInfo.PatternFrames; k++) {
                            SpritePhase spritePhase = new() {
                                DurationMin = r.ReadUInt32(),
                                DurationMax = r.ReadUInt32()
                            };
                            spriteAnimation.SpritePhase.Add(spritePhase);
                        }

                        frameGroup.SpriteInfo.Animation = spriteAnimation;
                    } else {
                        // frame durations not present
                        // populate the animation info with values used in the old client
                        SpriteAnimation spriteAnimation = new() {
                            AnimationMode = ANIMATION_ANIMATION_MODE.AnimationAsynchronized,
                            LoopCount = 1,
                            DefaultStartPhase = 1
                        };

                        for (int k = 0; k < frameGroup.SpriteInfo.PatternFrames; k++) {
                            SpritePhase spritePhase = new() {
                                DurationMin = 100,
                                DurationMax = 100
                            };
                            spriteAnimation.SpritePhase.Add(spritePhase);
                        }

                        frameGroup.SpriteInfo.Animation = spriteAnimation;
                    }
                }

                int NumSprites = (int)(frameGroup.SpriteInfo.PatternWidth * frameGroup.SpriteInfo.PatternHeight * frameGroup.SpriteInfo.PatternLayers * frameGroup.SpriteInfo.PatternX * frameGroup.SpriteInfo.PatternY * frameGroup.SpriteInfo.PatternZ * frameGroup.SpriteInfo.PatternFrames);
                for (var x = 0; x < NumSprites; x++) {
                    var sprite = isExtended ? r.ReadUInt32() : r.ReadUInt16();
                    frameGroup.SpriteInfo.SpriteId.Add(sprite);

                }
                appearance.FrameGroup.Add(frameGroup);
            }

            return appearance;
        }

        public static void ReadAppearanceAttr(Appearance appearance, BinaryReader r, VersionInfo versionInfo)
        {
            byte opt;
            while ((opt = r.ReadByte()) != 0xFF)
            {
                if (!versionInfo.FlagsById.TryGetValue(opt, out FlagInfo flagInfo))
                {
                    throw new Exception($"Unknown flag {opt} for version {versionInfo.Name}");
                }
                switch (flagInfo.Name)
                {
                    case "Ground":
                        appearance.Flags.Bank = new AppearanceFlagBank
                        {
                            Waypoints = r.ReadUInt16()
                        };
                        break;

                    case "Clip": // order 1
                        appearance.Flags.Clip = true;
                        break;

                    case "Top": // order 5
                        appearance.Flags.Top = true;
                        break;

                    case "Bottom": // order 2
                        appearance.Flags.Bottom = true;
                        break;

                    case "Container":
                        appearance.Flags.Container = true;
                        break;

                    case "Stackable":
                        appearance.Flags.Cumulative = true;
                        break;

                    case "Usable":
                        appearance.Flags.Usable = true;
                        break;

                    case "ForceUse":
                        appearance.Flags.Forceuse = true;
                        break;

                    case "Multiuse":
                        appearance.Flags.Multiuse = true;
                        break;

                    case "Writeable":
                        appearance.Flags.Write = new AppearanceFlagWrite
                        {
                            MaxTextLength = r.ReadUInt16()
                        };
                        break;

                    case "WriteableOnce":
                        appearance.Flags.WriteOnce = new AppearanceFlagWriteOnce
                        {
                            MaxTextLengthOnce = r.ReadUInt16()
                        };
                        break;

                    case "LiquidPool":
                        appearance.Flags.Liquidpool = true;
                        break;

                    case "LiquidContainer":
                        appearance.Flags.Liquidcontainer = true;
                        break;

                    case "Impassable":
                        appearance.Flags.Unpass = true;
                        break;

                    case "Unmovable":
                        appearance.Flags.Unmove = true;
                        break;

                    case "BlocksSight":
                        appearance.Flags.Unsight = true;
                        break;

                    case "BlocksPathfinding":
                        appearance.Flags.Avoid = true;
                        break;

                    case "NoMovementAnimation":
                        appearance.Flags.NoMovementAnimation = true;
                        break;

                    case "Pickupable":
                        appearance.Flags.Take = true;
                        break;

                    case "Hangable":
                        appearance.Flags.Hang = true;
                        break;

                    case "HooksSouth":
                        appearance.Flags.HookSouth = true;
                        break;

                    case "HooksEast":
                        appearance.Flags.HookEast = true;
                        break;

                    case "Rotateable":
                        appearance.Flags.Rotate = true;
                        break;

                    case "LightSource":
                        appearance.Flags.Light = new AppearanceFlagLight
                        {
                            Brightness = r.ReadUInt16(),
                            Color = r.ReadUInt16()
                        };
                        break;

                    case "AlwaysSeen":
                        appearance.Flags.DontHide = true;
                        break;

                    case "Translucent":
                        appearance.Flags.Translucent = true;
                        break;

                    case "Displaced":
                        if (flagInfo.Version == 2) {
                            // 1098 standard - read offset
                            appearance.Flags.Shift = new() {
                                X = r.ReadInt16(),
                                Y = r.ReadInt16(),
                                A = 0,
                                B = 0
                            };
                        } else if (flagInfo.Version == 3) {
                            // RD standard - displacement + sprite offset (?)
                            appearance.Flags.Shift = new() {
                                X = r.ReadInt16(),
                                A = r.ReadInt16(),
                                Y = r.ReadInt16(),
                                B = r.ReadInt16()
                            };
                        } else {
                            // old elevation did not precise the offset
                            appearance.Flags.Shift = new() {
                                X = 8,
                                Y = 8,
                                A = 0,
                                B = 0
                            };
                        }

                        break;

                    case "Elevated":
                        appearance.Flags.Height = new AppearanceFlagHeight
                        {
                            Elevation = r.ReadUInt16(),
                        };
                        break;

                    case "LyingObject":
                        appearance.Flags.LyingObject = true;
                        break;

                    case "AlwaysAnimated":
                        appearance.Flags.AnimateAlways = true;
                        break;

                    case "MinimapColor":
                        appearance.Flags.Automap = new AppearanceFlagAutomap
                        {
                            Color = r.ReadUInt16(),
                        };
                        break;

                    case "FullTile":
                        appearance.Flags.Fullbank = true;
                        break;

                    case "HelpInfo":
                        appearance.Flags.Lenshelp = new AppearanceFlagLenshelp
                        {
                            Id = r.ReadUInt16(),
                        };
                        break;

                    case "Lookthrough":
                        appearance.Flags.IgnoreLook = true;
                        break;

                    case "Clothes":
                        appearance.Flags.Clothes = new AppearanceFlagClothes
                        {
                            Slot = r.ReadUInt16(),
                        };
                        break;

                    case "DefaultAction":
                        appearance.Flags.DefaultAction = new AppearanceFlagDefaultAction
                        {
                            Action = (PLAYER_ACTION)r.ReadUInt16(),
                        };
                        break;

                    case "Market":
                        appearance.Flags.Market = new AppearanceFlagMarket
                        {
                            Category = (ITEM_CATEGORY)r.ReadUInt16(),
                            TradeAsObjectId = r.ReadUInt16(),
                            ShowAsObjectId = r.ReadUInt16(),
                        };

                        ushort MarketNameSize = r.ReadUInt16();
                        byte[] buffer = r.ReadBytes(MarketNameSize);
                        appearance.Name = Encoding.GetEncoding("ISO-8859-1").GetString(buffer, 0, buffer.Length);

                        ushort MarketProfession = r.ReadUInt16();
                        appearance.Flags.Market.Vocation = (VOCATION)MarketProfession;

                        appearance.Flags.Market.MinimumLevel = r.ReadUInt16();

                        break;

                    case "Wrappable":
                        appearance.Flags.Wrap = true;
                        break;

                    case "UnWrappable":
                        appearance.Flags.Unwrap = true;
                        break;

                    case "TopEffect":
                        appearance.Flags.Topeffect = true;
                        break;

                    case "ShowCharges":
                        appearance.Flags.Wearout = true;
                        break;

                    case "WingsOffset":
                        appearance.Flags.WingsOffset = new() {
                            NorthX = r.ReadInt16(),
                            NorthY = r.ReadInt16(),
                            EastX = r.ReadInt16(),
                            EastY = r.ReadInt16(),
                            SouthX = r.ReadInt16(),
                            SouthY = r.ReadInt16(),
                            WestX = r.ReadInt16(),
                            WestY = r.ReadInt16(),
                        };
                        break;

                    case "Default":
                        //ret.Default = true;
                        break;

                    default:
                        throw new Exception("Unknown appearance attribute " + opt);

                }
            }
        }
        public static void WriteAppearance(BinaryWriter w, Appearance item, VersionInfo versionInfo, PresetSettings preset)
        {
            WriteAppearanceAttr(w, item, versionInfo);

            bool isExtended = preset.Extended;
            bool hasFrameDurations = preset.FrameDurations;
            bool hasFrameGroups = preset.FrameGroups;

            byte FrameGroupCount = 1;

            if (hasFrameGroups && item.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit) {
                FrameGroupCount = (byte)item.FrameGroup.Count;
                w.Write((byte)FrameGroupCount);
            }

            for (int i = 0; i < FrameGroupCount; i++) {
                SpriteInfo spriteInfo = item.FrameGroup[i].SpriteInfo;

                if (hasFrameGroups && item.AppearanceType == APPEARANCE_TYPE.AppearanceOutfit) {
                    w.Write((byte)item.FrameGroup[i].FixedFrameGroup);
                }

                byte Width = (byte)spriteInfo.PatternWidth;
                byte Height = (byte)spriteInfo.PatternHeight;

                w.Write(Width);
                w.Write(Height);

                if (Width > 1 || Height > 1) {
                    w.Write((byte)spriteInfo.PatternSize);
                }

                w.Write((byte)spriteInfo.PatternLayers);
                w.Write((byte)spriteInfo.PatternX);
                w.Write((byte)spriteInfo.PatternY);

                byte patternZ = 1;
                if (versionInfo.UsePatternZ) {
                    patternZ = (byte)spriteInfo.PatternZ;
                    w.Write((byte)patternZ);
                }

                w.Write((byte)spriteInfo.PatternFrames);

                if (hasFrameDurations && spriteInfo.PatternFrames > 1) {
                    SpriteAnimation animation = spriteInfo.Animation;

                    // loop count is stored as-is: 0 = infinite, 0xFFFFFFFF (-1) = ping-pong, n = counted;
                    // it is independent of the (a)synchronous animation mode
                    w.Write((byte)animation.AnimationMode);
                    w.Write(animation.LoopCount);
                    w.Write((byte)animation.DefaultStartPhase);

                    // the reader expects exactly PatternFrames phases
                    for (int k = 0; k < spriteInfo.PatternFrames; k++) {
                        SpritePhase? phase = k < animation.SpritePhase.Count ? animation.SpritePhase[k] : null;
                        w.Write(phase?.DurationMin ?? 100u);
                        w.Write(phase?.DurationMax ?? 100u);
                    }
                }

                // some asset packs have broken outfits
                // using the number of sprites declared by the object
                // and filling the missing slots with sprite 0
                // prevents generating a broken dat file
                uint NumSprites = spriteInfo.PatternWidth
                                * spriteInfo.PatternHeight
                                * spriteInfo.PatternLayers
                                * spriteInfo.PatternX
                                * spriteInfo.PatternY
                                * patternZ
                                * spriteInfo.PatternFrames;

                for (var x = 0; x < NumSprites; x++) {
                    uint spriteId = 0;
                    if (x < spriteInfo.SpriteId.Count) {
                        spriteId = spriteInfo.SpriteId[x];
                    }

                    if (isExtended) {
                        w.Write(spriteId);
                    } else {
                        w.Write((ushort)spriteId);
                    }
                }
            }
        }

        public static void WriteAppearanceAttr(BinaryWriter output, Appearance item, VersionInfo versionInfo)
        {
            // some appearances may have flags not set
            if (item.Flags == null)
            {
                // end of flags
                output.Write((byte)0xFF);
                return;
            }

            // flags are collected first and emitted in ascending id order, like the original files;
            // the order of the checks below does not match the id order of every structure
            using var buffer = new MemoryStream();
            using var w = new BinaryWriter(buffer);
            var segments = new List<(byte Id, long Start)>();
            void Begin(string name)
            {
                byte id = (byte)versionInfo.GetFlagId(name);
                segments.Add((id, buffer.Position));
                w.Write(id);
            }

            if (item.Flags.Bank != null && versionInfo.HasFlag("Ground"))
            {
                Begin("Ground");
                // the ground speed is mandatory in the dat - omitting it shifts every following byte
                w.Write((ushort)item.Flags.Bank.Waypoints);
            }

            if (item.Flags.Clip && versionInfo.HasFlag("Clip"))
                Begin("Clip");

            if (item.Flags.Top && versionInfo.HasFlag("Top"))
                Begin("Top");

            if (item.Flags.Bottom && versionInfo.HasFlag("Bottom"))
                Begin("Bottom");

            if (item.Flags.Container && versionInfo.HasFlag("Container"))
                Begin("Container");

            if (item.Flags.Cumulative && versionInfo.HasFlag("Stackable"))
                Begin("Stackable");

            if (item.Flags.Usable && versionInfo.HasFlag("Usable"))
                Begin("Usable");

            if (item.Flags.Forceuse && versionInfo.HasFlag("ForceUse"))
                Begin("ForceUse");

            if (item.Flags.Multiuse && versionInfo.HasFlag("Multiuse"))
                Begin("Multiuse");

            if (item.Flags.Write != null && versionInfo.HasFlag("Writeable"))
            {
                Begin("Writeable");
                w.Write((ushort)item.Flags.Write.MaxTextLength);
            }

            if (item.Flags.WriteOnce != null && versionInfo.HasFlag("WriteableOnce"))
            {
                Begin("WriteableOnce");
                w.Write((ushort)item.Flags.WriteOnce.MaxTextLengthOnce);
            }

            if (item.Flags.Liquidpool && versionInfo.HasFlag("LiquidPool"))
                Begin("LiquidPool");

            if (item.Flags.Liquidcontainer && versionInfo.HasFlag("LiquidContainer"))
                Begin("LiquidContainer");

            if (item.Flags.Unpass && versionInfo.HasFlag("Impassable"))
                Begin("Impassable");

            if (item.Flags.Unmove && versionInfo.HasFlag("Unmovable"))
                Begin("Unmovable");

            if (item.Flags.Unsight && versionInfo.HasFlag("BlocksSight"))
                Begin("BlocksSight");

            if (item.Flags.Avoid && versionInfo.HasFlag("BlocksPathfinding"))
                Begin("BlocksPathfinding");

            if (item.Flags.NoMovementAnimation && versionInfo.HasFlag("NoMovementAnimation"))
                Begin("NoMovementAnimation");

            if (item.Flags.Take && versionInfo.HasFlag("Pickupable"))
                Begin("Pickupable");

            if (item.Flags.Hang && versionInfo.HasFlag("Hangable"))
                Begin("Hangable");

            if (item.Flags.HookSouth && versionInfo.HasFlag("HooksSouth"))
                Begin("HooksSouth");

            if (item.Flags.HookEast && versionInfo.HasFlag("HooksEast"))
                Begin("HooksEast");

            if (item.Flags.Rotate && versionInfo.HasFlag("Rotateable"))
                Begin("Rotateable");

            if (item.Flags.Light != null && versionInfo.HasFlag("LightSource"))
            {
                Begin("LightSource");
                w.Write((ushort)item.Flags.Light.Brightness);
                w.Write((ushort)item.Flags.Light.Color);
            }

            if (item.Flags.DontHide && versionInfo.HasFlag("AlwaysSeen"))
                Begin("AlwaysSeen");

            if (item.Flags.Translucent && versionInfo.HasFlag("Translucent"))
                Begin("Translucent");

            if (item.Flags.Shift != null && versionInfo.HasFlag("Displaced"))
            {
                byte flagId = (byte)versionInfo.GetFlagId("Displaced");
                if (!versionInfo.FlagsById.TryGetValue(flagId, out FlagInfo flagInfo)) {
                    throw new Exception($"Failed to get the structure of flag \"Displaced\".");
                }

                Begin("Displaced");
                switch (flagInfo.Version) {
                    case 2:
                        // 1098 standard
                        w.Write((short)item.Flags.Shift.X);
                        w.Write((short)item.Flags.Shift.Y);
                        break;
                    case 3:
                        // RD standard - displacement + sprite offset (?)
                        w.Write((short)item.Flags.Shift.X);
                        w.Write((short)item.Flags.Shift.A);
                        w.Write((short)item.Flags.Shift.Y);
                        w.Write((short)item.Flags.Shift.B);
                        break;
                    default:
                        // old elevation did not precise the offset
                        break;
                }
            }

            if (item.Flags.Height != null && versionInfo.HasFlag("Elevated"))
            {
                Begin("Elevated");
                w.Write((ushort)item.Flags.Height.Elevation);
            }

            if (item.Flags.LyingObject && versionInfo.HasFlag("LyingObject"))
                Begin("LyingObject");

            if (item.Flags.AnimateAlways && versionInfo.HasFlag("AlwaysAnimated"))
                Begin("AlwaysAnimated");

            if (item.Flags.Automap != null && versionInfo.HasFlag("MinimapColor"))
            {
                Begin("MinimapColor");
                w.Write((ushort)item.Flags.Automap.Color);
            }

            if (item.Flags.Fullbank && versionInfo.HasFlag("FullTile"))
                Begin("FullTile");

            if (item.Flags.Lenshelp != null && versionInfo.HasFlag("HelpInfo"))
            {
                Begin("HelpInfo");
                w.Write((ushort)item.Flags.Lenshelp.Id);
            }

            if (item.Flags.IgnoreLook && versionInfo.HasFlag("Lookthrough"))
                Begin("Lookthrough");

            if (item.Flags.Clothes != null && versionInfo.HasFlag("Clothes"))
            {
                Begin("Clothes");
                w.Write((ushort)item.Flags.Clothes.Slot);
            }

            if (item.Flags.DefaultAction != null && versionInfo.HasFlag("DefaultAction")) {
                Begin("DefaultAction");
                w.Write((ushort)item.Flags.DefaultAction.Action);
            }

            if (item.Flags.Market != null && versionInfo.HasFlag("Market"))
            {
                Begin("Market");

                w.Write((ushort)item.Flags.Market.Category);
                w.Write((ushort)item.Flags.Market.TradeAsObjectId);
                w.Write((ushort)item.Flags.Market.ShowAsObjectId);

                // the reader decodes ISO-8859-1; BinaryWriter.Write(char) would emit UTF-8 and
                // desync the declared length from the bytes written for any non-ASCII name
                byte[] name = Encoding.GetEncoding("ISO-8859-1").GetBytes(item.Name ?? "");
                w.Write((ushort)name.Length);
                w.Write(name);
                w.Write((ushort)item.Flags.Market.Vocation);
                w.Write((ushort)item.Flags.Market.MinimumLevel);
            }

            if (item.Flags.Wrap && versionInfo.HasFlag("Wrappable"))
                Begin("Wrappable");

            if (item.Flags.Unwrap && versionInfo.HasFlag("UnWrappable"))
                Begin("UnWrappable");

            if (item.Flags.Topeffect && versionInfo.HasFlag("TopEffect"))
                Begin("TopEffect");

            // flag "rune charges visible" - dat structure 7.8 - 8.54
            if (item.Flags.Wearout && versionInfo.HasFlag("ShowCharges"))
                Begin("ShowCharges");

            // otc wings offset
            if (item.Flags.WingsOffset != null && versionInfo.HasFlag("WingsOffset")) {
                Begin("WingsOffset");
                w.Write((short)item.Flags.WingsOffset.NorthX);
                w.Write((short)item.Flags.WingsOffset.NorthY);
                w.Write((short)item.Flags.WingsOffset.EastX);
                w.Write((short)item.Flags.WingsOffset.EastY);
                w.Write((short)item.Flags.WingsOffset.SouthX);
                w.Write((short)item.Flags.WingsOffset.SouthY);
                w.Write((short)item.Flags.WingsOffset.WestX);
                w.Write((short)item.Flags.WingsOffset.WestY);
            }

            w.Flush();
            byte[] bytes = buffer.ToArray();
            // OrderBy is stable, so a flag written twice keeps its relative order
            foreach (int k in Enumerable.Range(0, segments.Count).OrderBy(i => segments[i].Id)) {
                long start = segments[k].Start;
                long end = k + 1 < segments.Count ? segments[k + 1].Start : bytes.Length;
                output.Write(bytes, (int)start, (int)(end - start));
            }
            output.Write((byte)0xFF);
        }

    }
}