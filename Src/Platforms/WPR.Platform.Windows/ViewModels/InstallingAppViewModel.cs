using Avalonia.Media.Imaging;
using ReactiveUI;
using System;
using System.IO;
using System.Reactive;

namespace WPR.Platform.Windows.ViewModels
{
    public class InstallingAppViewModel : ViewModelBase
    {
        private int _Progress;
        private readonly Bitmap? _Icon;

        public string Name { get; }
        public string Author { get; }
        public string Publisher { get; }
        public string Description { get; }
        public string Version { get; }
        /// <summary>
        /// Product ID of the app being installed. Used by the listing view-model
        /// to suppress the corresponding "available" library entry while the
        /// install is in flight (and restore it on cancel/failure).
        /// </summary>
        public string ProductId { get; }
        public WPR.Models.ApplicationType? ApplicationType { get; }

        public Bitmap? Icon => _Icon;

        public int Progress
        {
            get => _Progress;
            set => this.RaiseAndSetIfChanged(ref _Progress, value);
        }

        public string Tooltip => string.IsNullOrEmpty(Description) ? Name : $"{Name}\n\n{Description}";

        public ReactiveCommand<Unit, Unit> CancelCommand { get; }

        public InstallingAppViewModel(ApplicationPreview preview, Action onCancel)
        {
            Name = preview.Name;
            Author = preview.Author;
            Publisher = preview.Publisher;
            Description = preview.Description;
            Version = preview.Version;
            ProductId = preview.ProductId;
            ApplicationType = preview.ApplicationType;

            if (preview.IconBytes != null)
            {
                try
                {
                    using MemoryStream ms = new MemoryStream(preview.IconBytes);
                    _Icon = new Bitmap(ms);
                }
                catch { }
            }

            CancelCommand = ReactiveCommand.Create(onCancel);
        }
    }
}
