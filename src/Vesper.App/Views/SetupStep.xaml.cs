using Microsoft.UI.Xaml.Controls;

namespace Vesper.Views;

public sealed partial class SetupStep : UserControl
{
    public SetupStep() => InitializeComponent();

    public string Number
    {
        get => NumberText.Text;
        set => NumberText.Text = value;
    }

    public string Text
    {
        get => BodyText.Text;
        set => BodyText.Text = value;
    }
}
