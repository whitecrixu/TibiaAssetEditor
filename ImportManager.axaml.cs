using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    public partial class ImportManager : Window
    {
        private LegacyDatEditor _editor;

        public ImportManager(LegacyDatEditor editor)
        {
            InitializeComponent();
            _editor = editor;
            Closed += ImportManager_Closed;
        }

        private Appearances? ImportAppearances;
        private Dictionary<uint, Sprite> sprites = [];
        private List<ShowList> AllSprList = [];
        private ConcurrentDictionary<int, MemoryStream> SprLists = [];
        private ObservableCollection<ShowList> ThingsOutfit = [];
        private ObservableCollection<ShowList> ThingsItem = [];
        private ObservableCollection<ShowList> ThingsEffect = [];
        private ObservableCollection<ShowList> ThingsMissile = [];
        private static SpriteStorage? MainSprStorage;

        private void ImportManager_Closed(object? sender, EventArgs e)
        {
            Closed -= ImportManager_Closed;
            sprites.Clear();
            SprLists.Clear();
            AllSprList.Clear();
            ThingsOutfit.Clear();
            ThingsItem.Clear();
            ThingsEffect.Clear();
            ThingsMissile.Clear();
            GC.Collect();
        }

        private async void OpenClient_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Client Directory"
            });

            if (folders.Count == 0) return;

            string assetsPath = folders[0].Path.LocalPath;
            if (!assetsPath.EndsWith(Path.DirectorySeparatorChar))
                assetsPath += Path.DirectorySeparatorChar;

            ClientPathText.Text = assetsPath;

            string datPath = Path.Combine(assetsPath, "Tibia.dat");
            string sprPath = Path.Combine(assetsPath, "Tibia.spr");

            if (!File.Exists(datPath) || !File.Exists(sprPath))
            {
                ErrorManager.ShowError("Tibia.dat or Tibia.spr not found in selected directory.");
                return;
            }

            var progressReporter = new Progress<int>(value =>
            {
                Dispatcher.UIThread.InvokeAsync(() => LoadProgress.Value = value);
            });

            await Task.Run(() =>
            {
                LegacyAppearance dat = new();
                dat.ReadLegacyDat(datPath, MainWindow.GetCurrentLoadedVersion());
                ImportAppearances = dat.Appearances;

                bool transparency = false;
                Dispatcher.UIThread.InvokeAsync(() =>
                    transparency = SprTransparent.IsChecked ?? false).Wait();

                MainSprStorage = new SpriteStorage(sprPath, transparency, progressReporter);
                MainSprStorage.LoadSprites();
                SprLists = MainSprStorage.SprLists;
                sprites = MainSprStorage.Sprites;
                for (uint i = 0; i < sprites.Count; i++)
                    AllSprList.Add(new ShowList() { Id = i });
            });

            foreach (var outfit in ImportAppearances!.Outfit)
                ThingsOutfit.Add(new ShowList() { Id = outfit.Id });
            foreach (var item in ImportAppearances.Object)
                ThingsItem.Add(new ShowList() { Id = item.Id });
            foreach (var effect in ImportAppearances.Effect)
                ThingsEffect.Add(new ShowList() { Id = effect.Id });
            foreach (var missile in ImportAppearances.Missile)
                ThingsMissile.Add(new ShowList() { Id = missile.Id });

            SprListView.ItemsSource = AllSprList;
            UpdateShowList(ObjectMenu.SelectedIndex);
        }

        private void UpdateShowList(int selection)
        {
            if (ObjListView == null) return;

            if (selection == 0)
                ObjListView.ItemsSource = ThingsOutfit;
            else if (selection == 1)
                ObjListView.ItemsSource = ThingsItem;
            else if (selection == 2)
                ObjListView.ItemsSource = ThingsEffect;
            else if (selection == 3)
                ObjListView.ItemsSource = ThingsMissile;

            if (ObjListView.ItemCount > 0)
                ObjListView.SelectedIndex = 0;
        }

        private void ObjectMenuChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateShowList(ObjectMenu.SelectedIndex);
        }

        private void ObjListView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ObjListView.SelectedItem is ShowList showList)
                ObjListViewSelectedIndex.Value = (int)showList.Id;
        }

        private void ObjectImport_Click(object? sender, RoutedEventArgs e)
        {
            if (ImportAppearances == null || ObjListView.SelectedItem is not ShowList selected) return;

            Appearance? appearance = ObjectMenu.SelectedIndex switch
            {
                0 => ImportAppearances.Outfit.FirstOrDefault(o => o.Id == selected.Id),
                1 => ImportAppearances.Object.FirstOrDefault(o => o.Id == selected.Id),
                2 => ImportAppearances.Effect.FirstOrDefault(o => o.Id == selected.Id),
                3 => ImportAppearances.Missile.FirstOrDefault(o => o.Id == selected.Id),
                _ => null
            };

            if (appearance == null) return;

            // Import the appearance into main editor
            var target = ObjectMenu.SelectedIndex switch
            {
                0 => MainWindow.appearances?.Outfit,
                1 => MainWindow.appearances?.Object,
                2 => MainWindow.appearances?.Effect,
                3 => MainWindow.appearances?.Missile,
                _ => null
            };

            if (target != null)
            {
                target.Add(appearance.Clone());
                ErrorManager.ShowInfo($"Imported object {selected.Id}");
                _editor.UpdateShowList(ObjectMenu.SelectedIndex);
            }
        }

        private void ImportSpr_Click(object? sender, RoutedEventArgs e)
        {
            // TODO: Import sprite
            ErrorManager.ShowInfo("Sprite import - coming in future update");
        }

        private void ImportObject_Click(object? sender, RoutedEventArgs e)
        {
            // TODO: OBD import
            ErrorManager.ShowInfo("OBD import - coming in future update");
        }

        private void ConvertObject(object? sender, RoutedEventArgs e) { /* TODO */ }
    }
}
