using System.Collections.Generic;
using System.Linq;

namespace Assets_Editor;

/// <summary>
/// Signatures of the official Tibia clients that use a legacy Tibia.dat / Tibia.spr, so a loaded file can
/// be named and read with the right structure at once. Shared by the desktop editor (Windows, Linux) and
/// the Android app.
///
/// Source: Object Builder's versions.xml (MIT; github.com/ottools/ObjectBuilder, extended in
/// github.com/punkice3407/ObjectBuilder) plus 10.79 / 10.80 from github.com/ottools/ClientConverter.
/// A few signatures belong to two versions (7.60 / 7.70, 8.50 v2 / 8.52, ...); they always share a structure.
/// Custom OTS clients usually keep the signature of the version they are based on.
/// </summary>
public static class KnownClients
{
    public sealed record ClientVersion(int Version, string Name, uint DatSignature, uint SprSignature)
    {
        /// <summary>The structure number in Appearances.xml.</summary>
        public int Structure => Version switch
        {
            < 740 => 1,  // 7.1 - 7.3
            < 755 => 2,  // 7.4 - 7.5
            < 780 => 3,  // 7.55 - 7.72
            < 860 => 4,  // 7.8 - 8.54
            < 1010 => 5, // 8.6 - 9.86
            _ => 6,      // 10.10 +
        };

        // the layout flags the official client uses (Object Builder's defaults); a .otfi overrides them
        public bool Extended => Version >= 960;
        public bool FrameDurations => Version >= 1050;
        public bool FrameGroups => Version >= 1057;
    }

    public static readonly IReadOnlyList<ClientVersion> All =
    [
        new(710, "7.10", 0x3DFF4B2A, 0x3DFF4AEB),
        new(730, "7.30", 0x411A6233, 0x411A6279),
        new(740, "7.40", 0x41BF619C, 0x41B9EA86),
        new(750, "7.50", 0x42F81973, 0x42F81949),
        new(755, "7.55", 0x437B2B8F, 0x434F9CDE),
        new(760, "7.60", 0x439D5A33, 0x439852BE),
        new(770, "7.70", 0x439D5A33, 0x439852BE),
        new(780, "7.80", 0x44CE4743, 0x44CE4206),
        new(790, "7.90", 0x457D854E, 0x457957C8),
        new(792, "7.92", 0x459E7B73, 0x45880FE8),
        new(800, "8.00", 0x467FD7E6, 0x467F9E74),
        new(810, "8.10", 0x475D3747, 0x475D0B01),
        new(811, "8.11", 0x47F60E37, 0x47EBB9B2),
        new(820, "8.20", 0x486905AA, 0x4868ECC9),
        new(830, "8.30", 0x48DA1FB6, 0x48C8E712),
        new(840, "8.40", 0x493D607A, 0x493D4E7C),
        new(841, "8.41", 0x49B7CC19, 0x49B140EA),
        new(842, "8.42", 0x49C233C9, 0x49B140EA),
        new(850, "8.50 v1", 0x4A49C5EB, 0x4A44FD4E),
        new(850, "8.50 v2", 0x4A4CC0DC, 0x4A44FD4E),
        new(850, "8.50 v3", 0x4AE97492, 0x4ACB5230),
        new(852, "8.52", 0x4A4CC0DC, 0x4A44FD4E),
        new(853, "8.53", 0x4AE97492, 0x4ACB5230),
        new(854, "8.54 v1", 0x4B1E2CAA, 0x4B1E2C87),
        new(854, "8.54 v2", 0x4B0D46A9, 0x4B0D3AFF),
        new(854, "8.54 v3", 0x4B28B89E, 0x4B1E2C87),
        new(855, "8.55", 0x4B98FF53, 0x4B913871),
        new(860, "8.60 v1", 0x4C28B721, 0x4C220594),
        new(860, "8.60 v2", 0x4C2C7993, 0x4C220594),
        new(861, "8.61", 0x4C6A4CBC, 0x4C63F145),
        new(862, "8.62", 0x4C973450, 0x4C63F145),
        new(870, "8.70", 0x4CFE22C5, 0x4CFD078A),
        new(871, "8.71", 0x4D41979E, 0x4D3D65D0),
        new(872, "8.72", 0x4DAD1A1A, 0x4DAD1A32),
        new(900, "9.00", 0x4DBAA20B, 0x4DAD1A32),
        new(910, "9.10", 0x4E12DAFF, 0x4E12DB27),
        new(920, "9.20", 0x4E807C08, 0x4E807C23),
        new(940, "9.40", 0x4EE71DE5, 0x4EE71E06),
        new(944, "9.44 v0", 0x4F0EEFBB, 0x4F0EEFEF),
        new(944, "9.44 v1", 0x4F105168, 0x4F1051D7),
        new(944, "9.44 v2", 0x4F16C0D7, 0x4F1051D7),
        new(944, "9.44 v3", 0x4F3131CF, 0x4F3131F6),
        new(946, "9.46", 0x4F75B7AB, 0x4F5DCEF7),
        new(950, "9.50", 0x4F75B7AB, 0x4F75B7CD),
        new(952, "9.52", 0x4F857F6C, 0x4F857F8E),
        new(953, "9.53", 0x4FA11252, 0x4FA11282),
        new(954, "9.54", 0x4FD5956B, 0x4FD595B7),
        new(960, "9.60", 0x4FFA74CC, 0x4FFA74F9),
        new(961, "9.61", 0x50226F9D, 0x50226FBD),
        new(963, "9.63", 0x503CB933, 0x503CB954),
        new(970, "9.70", 0x5072A490, 0x5072A567),
        new(980, "9.80", 0x50C70674, 0x50C70753),
        new(981, "9.81", 0x50D1C5B6, 0x50D1C685),
        new(982, "9.82", 0x512CAD09, 0x512CAD68),
        new(983, "9.83", 0x51407B67, 0x51407BC7),
        new(985, "9.85", 0x51641A1B, 0x51641A84),
        new(986, "9.86", 0x5170E904, 0x5170E96F),
        new(1010, "10.10", 0x51E3F8C3, 0x51E3F8E9),
        new(1020, "10.20", 0x5236F129, 0x5236F14F),
        new(1021, "10.21", 0x526A5068, 0x526A5090),
        new(1030, "10.30", 0x52A59036, 0x52A5905F),
        new(1031, "10.31", 0x52AED581, 0x52AED5A7),
        new(1032, "10.32", 0x52D8D0A9, 0x52D8D0CE),
        new(1034, "10.34", 0x52E74AB5, 0x52E74ADA),
        new(1035, "10.35", 0x52FDFC2C, 0x52FDFC54),
        new(1036, "10.36", 0x53159C7E, 0x53159CA9),
        new(1037, "10.37", 0x531EA82E, 0x531EA856),
        new(1038, "10.38", 0x5333C199, 0x5333C1C3),
        new(1039, "10.39", 0x535A50AD, 0x535A50D5),
        new(1040, "10.40", 0x5379984D, 0x53799876),
        new(1041, "10.41", 0x5383504E, 0x53835077),
        new(1050, "10.50", 0x53B6460E, 0x53B64639),
        new(1051, "10.51", 0x53C8CC17, 0x53C8CC3F),
        new(1052, "10.52", 0x53E898BD, 0x53E898E5),
        new(1053, "10.53", 0x53FAD76E, 0x53FAD799),
        new(1054, "10.54", 0x540D3A47, 0x53E898E5),
        new(1055, "10.55", 0x54128727, 0x54128755),
        new(1056, "10.56", 0x542143B0, 0x542143DE),
        new(1057, "10.57", 0x542535F9, 0x54253627),
        new(1058, "10.58", 0x542D12E7, 0x542D1315),
        new(1059, "10.59", 0x5434084B, 0x54340879),
        new(1060, "10.60", 0x5448D9C7, 0x5448DA10),
        new(1061, "10.61", 0x5448D9C7, 0x5448DA10),
        new(1062, "10.62", 0x54622638, 0x54622667),
        new(1063, "10.63", 0x546B502A, 0x546B505E),
        new(1064, "10.64", 0x547F05BE, 0x547F0632),
        new(1070, "10.70", 0x5481BB97, 0x5481BC06),
        new(1071, "10.71", 0x0000334F, 0x548E9EFE),
        new(1072, "10.72", 0x00003729, 0x54B37B99),
        new(1073, "10.73", 0x0000374D, 0x54BC95AE),
        new(1074, "10.74", 0x0000375E, 0x54C5FAB2),
        new(1075, "10.75", 0x00003775, 0x54D85085),
        new(1076, "10.76", 0x000037DF, 0x54F03CE9),
        new(1077, "10.77", 0x000038DE, 0x5525213D),
        new(1079, "10.79", 0x00003A71, 0x557A5E34),
        new(1080, "10.80", 0x00003B1A, 0x55A8BE2E),
        new(1090, "10.90", 0x00003F26, 0x565EE171),
        new(1091, "10.91", 0x00003F81, 0x56BC8198),
        new(1092, "10.92", 0x00004086, 0x570742B8),
        new(1093, "10.93", 0x0000413F, 0x5726E657),
        new(1093, "10.93 test", 0x000040FF, 0x57161DEA),
        new(1094, "10.94", 0x000041E5, 0x57459D43),
        new(1095, "10.95", 0x000041F3, 0x575A84BD),
        new(1098, "10.98", 0x000042A3, 0x57BBD603),
        new(1099, "10.99", 0x00004347, 0x57FF106B),
        new(1310, "13.10", 0x00004A10, 0x59E48E02),
    ];

    private static readonly Dictionary<uint, ClientVersion[]> ByDat = All.GroupBy(c => c.DatSignature).ToDictionary(g => g.Key, g => g.ToArray());

    /// <summary>The client with this dat signature, or null. Versions sharing a signature come back with a joined name.</summary>
    public static ClientVersion? FromDat(uint signature)
    {
        if (!ByDat.TryGetValue(signature, out ClientVersion[]? matches))
            return null;
        return matches.Length == 1 ? matches[0] : matches[0] with { Name = string.Join(" / ", matches.Select(m => m.Name)) };
    }
}
