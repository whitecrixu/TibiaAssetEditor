using System;

namespace Assets_Editor; 

public class Globals {
    // directory pickers
    // to generate more use tools -> create GUID in visual studio
    public static readonly Guid GUID_MainWindowAssetsPicker = new("43A0E8FA-B129-4DB4-AD2D-0C44C23CE222");

    // to do
    // public static readonly Guid GUID_MainWindowServerPicker = new("C01637F9-8C7B-4610-BBA2-530487BC57A2");

    public static readonly Guid GUID_DatEditor1 = new("820617D2-0ECA-4632-B62D-42F740BD731A"); // export as images path
    public static readonly Guid GUID_DatEditor2 = new("4FC8F7A5-4840-4840-A68B-26DFD955D224"); // export sprite as bitmap
    public static readonly Guid GUID_DatEditor3 = new("1A5860A3-5722-4FFC-B6F2-FCE4E9FE255F"); // import aec/obd
    public static readonly Guid GUID_DatEditor4 = new("5788DE66-9141-4995-9550-118FCFE88609"); // export as aec

    public static readonly Guid GUID_LegacyDatEditor1 = new("AB8D0A9E-F28F-4C63-8F76-EE41552BE4BB"); // export sprite as bitmap
    public static readonly Guid GUID_LegacyDatEditor2 = new("9BA45B21-456E-469C-9F15-1045455F348F"); // import sprite from bitmap
    public static readonly Guid GUID_LegacyDatEditor3 = new("268BE18A-5FCC-4D34-A8F0-B8643F9D3E77"); // replace sprite to selected bitmap

    public static readonly Guid GUID_ImportManager1 = new("72ED3998-51EC-4F28-8E39-846885C4FA7C"); // import from spr/dat
    public static readonly Guid GUID_ImportManager2 = new("0570A6C2-A004-4704-A627-E6F5A199494A"); // import from obd

    public static readonly Guid GUID_LuaWindowExportCSV = new("EABE038F-85C5-486E-8E76-AEAA0EB939F7"); // export lua output to CSV

    public static readonly Guid GUID_ObdDecoder_export = new("45F3020C-EF52-4CA0-8918-2E2C1CAAA96E"); // export to obd

    public static readonly Guid GUID_OtbEditor_load = new("C6314359-B3E6-43B0-B481-B60D4461BF9D"); // load otb
    public static readonly Guid GUID_OtbEditor_save = new("BFDCF4F6-ADE0-477D-AF45-472216ACBF47"); // save otb

    public static readonly Guid GUID_SprEditor_save = new("9AA10262-1929-47A2-85CE-A83183D8E620"); // save as bitmap

    public static readonly Guid GUID_ServerTools_ServerRoot = new("6B1E2D4A-93C7-4F0E-8A51-2C7D9E3B4F10"); // server root folder
    public static readonly Guid GUID_ServerTools_SaveReport = new("D3A84F62-1B9E-4C27-9F05-7E61A2C8B394"); // consistency report as TSV
    public static readonly Guid GUID_ServerTools_Manifest = new("8F27C1B5-4D6A-4E93-B0D8-51A9E7C3F2D6"); // update channel manifest
    public static readonly Guid GUID_ServerTools_OriginalDat = new("2C95E7A1-6F38-4B4D-8E12-A4D07B9C5E31"); // original dat for the things diff
    public static readonly Guid GUID_ServerTools_SaveDiff = new("A7D3F918-2E54-4A6B-9C81-3F0B6D2E7A45"); // things diff as JSON/TSV
    public static readonly Guid GUID_ServerTools_ServerFiles = new("5E4B9A27-C1D8-4F63-A2E7-9B18D5C3F604"); // server files for the export
    public static readonly Guid GUID_ServerTools_ThingsDir = new("F1C6A83D-7B29-4E50-8D4F-62E3A9B1C7D8"); // client things folder for the export
    public static readonly Guid GUID_ServerTools_ExportDir = new("39B7E5C2-A846-4D1F-B3E9-0C5A7F2D8B61"); // staging folder for the export
}
