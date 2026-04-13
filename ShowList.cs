using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Timers;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Assets_Editor
{
    public class ShowList : INotifyPropertyChanged
    {
        private uint id;
        private uint cid;
        private string name = "";

        private List<Bitmap?> images = new();
        private Bitmap? image;
        private Timer animationTimer;
        private int currentFrameIndex;
        private const double frameInterval = 100;
        private bool exported;

        public uint Id
        {
            get => id;
            set
            {
                if (id != value)
                {
                    id = value;
                    NotifyPropertyChanged(nameof(Id));
                }
            }
        }

        public uint Cid
        {
            get => cid;
            set
            {
                if (cid != value)
                {
                    cid = value;
                    NotifyPropertyChanged(nameof(Cid));
                }
            }
        }

        public List<Bitmap?> Images
        {
            get => images;
            set
            {
                if (images != value)
                {
                    images = value;
                    NotifyPropertyChanged(nameof(Images));
                    ResetAnimation();
                }
            }
        }

        public Bitmap? Image
        {
            get => image;
            set
            {
                if (image != value)
                {
                    image = value;
                    NotifyPropertyChanged(nameof(Image));
                }
            }
        }

        public bool Exported
        {
            get => exported;
            set
            {
                if (exported != value)
                {
                    exported = value;
                    NotifyPropertyChanged(nameof(Exported));
                }
            }
        }

        public string Name
        {
            get => name;
            set
            {
                if (name != value)
                {
                    name = value;
                    NotifyPropertyChanged(nameof(Name));
                }
            }
        }

        private string sizeLabel = "";
        public string SizeLabel
        {
            get => sizeLabel;
            set
            {
                if (sizeLabel != value)
                {
                    sizeLabel = value;
                    NotifyPropertyChanged(nameof(SizeLabel));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ShowList()
        {
            images = new List<Bitmap?>();
            animationTimer = new Timer(frameInterval);
            animationTimer.Elapsed += OnAnimationTick;
            animationTimer.AutoReset = true;
        }

        private void OnAnimationTick(object? sender, ElapsedEventArgs e)
        {
            if (Images == null || Images.Count == 0)
                return;

            try
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Image = Images[currentFrameIndex];
                    currentFrameIndex = (currentFrameIndex + 1) % Images.Count;
                });
            }
            catch
            {
                // app shutting down
            }
        }

        public void StartAnimation()
        {
            if (Images != null && Images.Count > 0)
            {
                currentFrameIndex = 0;
                Image = Images[currentFrameIndex];
                animationTimer.Start();
            }
        }

        public void StopAnimation()
        {
            animationTimer.Stop();
            currentFrameIndex = 0;
            if (Images != null && Images.Count > 0)
            {
                Image = Images[currentFrameIndex];
            }
        }

        private void ResetAnimation()
        {
            StopAnimation();
            StartAnimation();
        }

        protected void NotifyPropertyChanged(string propName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
