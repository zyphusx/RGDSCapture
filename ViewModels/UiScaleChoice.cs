using RGDSCapture.Services;

namespace RGDSCapture.ViewModels
{
    /// <summary>
    /// One entry in the UI Scale menu. Like <see cref="ThemeChoice"/>, it
    /// reports "active" by asking the service rather than caching a flag, so
    /// a factor set from the menu, a shortcut or settings keeps every entry
    /// in sync.
    /// </summary>
    public sealed class UiScaleChoice : ObservableObject
    {
        public UiScaleChoice(double scale, RelayCommand apply)
        {
            Scale = scale;
            ApplyCommand = apply;
        }

        public double Scale { get; }

        /// <summary>Percent label, e.g. 125%.</summary>
        public string Name => UiScaleService.Format(Scale);

        public RelayCommand ApplyCommand { get; }

        public bool IsActive => UiScaleService.IsCurrent(Scale);
        public void RaiseIsActive() => OnPropertyChanged(nameof(IsActive));
    }
}
