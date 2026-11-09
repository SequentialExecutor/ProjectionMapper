using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Controls;
using Image = System.Windows.Controls.Image;
using Binding = System.Windows.Data.Binding;

namespace ProjectionMapper.Views
{
    /// <summary>Observes the composed editor frame. It never attaches a renderer or changes projector routing.</summary>
    public partial class ProjectionPreviewWindow : Window
    {
        public ProjectionPreviewWindow(RenderHostControl source)
        {
            InitializeComponent();
            PreviewImage.SetBinding(Image.SourceProperty, new Binding(nameof(RenderHostControl.CurrentFrame)) { Source = source, Mode = BindingMode.OneWay });
            Closed += (_, _) => BindingOperations.ClearBinding(PreviewImage, Image.SourceProperty);
        }
        private void Preview_KeyDown(object sender, KeyEventArgs e)
        { if (e.Key is Key.Escape or Key.F5) { e.Handled = true; if (!e.IsRepeat) Close(); } }
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
