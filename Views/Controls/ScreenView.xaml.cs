using System.Windows.Controls;

namespace RGDSCapture.Views.Controls
{
    /// <summary>
    /// One screen panel: the video surface plus its health badge and stats
    /// overlay. Entirely declarative — everything it shows is bound to a
    /// <see cref="ViewModels.ScreenViewModel"/>.
    /// </summary>
    public partial class ScreenView : UserControl
    {
        public ScreenView() => InitializeComponent();
    }
}
