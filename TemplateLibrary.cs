using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    /// <summary>
    /// Reusable Appearance templates, stored as raw protobuf on disk.
    ///
    /// A template is a Cloned Appearance with Id=0 and SpriteData cleared — essentially
    /// just the flag set + frame group structure. When applied to a target appearance,
    /// the target's Id and sprite IDs are preserved, but flags/layout are overwritten.
    /// </summary>
    public static class TemplateLibrary
    {
        public static string TemplateDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TibiaAssetEditor", "templates");

        public static List<string> ListNames()
        {
            try
            {
                if (!Directory.Exists(TemplateDir)) return new List<string>();
                return Directory.GetFiles(TemplateDir, "*.aec")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => n != null)
                    .Cast<string>()
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        public static bool Save(string name, Appearance source)
        {
            try
            {
                Directory.CreateDirectory(TemplateDir);
                string safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                string path = Path.Combine(TemplateDir, safe + ".aec");

                var clone = source.Clone();
                clone.ClearId();
                clone.SpriteData.Clear();
                // Also strip out sprite IDs — templates are about flags/structure, not pixels
                foreach (var fg in clone.FrameGroup)
                {
                    int n = fg.SpriteInfo.SpriteId.Count;
                    fg.SpriteInfo.SpriteId.Clear();
                    for (int i = 0; i < n; i++) fg.SpriteInfo.SpriteId.Add(0);
                }

                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
                clone.WriteTo(fs);
                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"Template save failed: {ex.Message}");
                return false;
            }
        }

        public static Appearance? Load(string name)
        {
            try
            {
                string path = Path.Combine(TemplateDir, name + ".aec");
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                return Appearance.Parser.ParseFrom(fs);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"Template load failed: {ex.Message}");
                return null;
            }
        }

        public static bool Delete(string name)
        {
            try
            {
                string path = Path.Combine(TemplateDir, name + ".aec");
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Apply the template to a target appearance. The target's Id and the sprite IDs
        /// in existing frame groups are preserved; flags and frame group structure are
        /// overwritten with the template's.
        /// </summary>
        public static void ApplyTo(Appearance target, Appearance template)
        {
            uint keepId = target.Id;
            var keepSpriteIds = new List<List<uint>>();
            foreach (var fg in target.FrameGroup)
                keepSpriteIds.Add(new List<uint>(fg.SpriteInfo.SpriteId));

            // Reset target to template state
            target.Flags = template.Flags?.Clone();
            if (template.HasName) target.Name = template.Name;
            if (template.HasDescription) target.Description = template.Description;
            target.FrameGroup.Clear();
            foreach (var fg in template.FrameGroup)
                target.FrameGroup.Add(fg.Clone());

            // Restore id
            target.Id = keepId;

            // Re-apply old sprite IDs into the new frame groups (same fg index, same slot count)
            for (int i = 0; i < target.FrameGroup.Count && i < keepSpriteIds.Count; i++)
            {
                var newSi = target.FrameGroup[i].SpriteInfo;
                var oldIds = keepSpriteIds[i];
                int count = Math.Min(newSi.SpriteId.Count, oldIds.Count);
                for (int s = 0; s < count; s++)
                    newSi.SpriteId[s] = oldIds[s];
            }
        }
    }
}
