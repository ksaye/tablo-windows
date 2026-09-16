using System.Windows.Controls;

namespace TabloWindows.Views;

public partial class GuideView : UserControl
{
    public GuideView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ViewModels.GuideViewModel vm)
                vm.ScrollRequested += offset =>
                    Dispatcher.BeginInvoke(() =>
                    {
                        BodySV.ScrollToHorizontalOffset(offset);
                        RulerSV.ScrollToHorizontalOffset(offset);
                    });
        };
    }

    // Keep the time ruler (horizontal) and channel labels (vertical) locked to the
    // program grid's scroll position.
    private void BodySV_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.HorizontalChange != 0) RulerSV.ScrollToHorizontalOffset(e.HorizontalOffset);
        if (e.VerticalChange != 0) LabelsSV.ScrollToVerticalOffset(e.VerticalOffset);
    }
}
