// The compact choice buttons under the prompt: Size, number of pictures and Quality show their value and open their options
// in a popup (the options are the same radio buttons as before, moved). Picking one closes it; labels follow every change,
// whether it came from a click, a model tab or a bundle.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ImageForge;

public partial class MainWindow
{
    void SizeChip_Click(object sender, RoutedEventArgs e) => SizePopup.IsOpen = true;
    void CountChip_Click(object sender, RoutedEventArgs e) => CountPopup.IsOpen = true;
    void QualityChip_Click(object sender, RoutedEventArgs e) => QualityPopup.IsOpen = true;

    void WatchChoices()
    {
        void Watch(Panel group, System.Windows.Controls.Primitives.Popup popup)
        {
            foreach (var r in group.Children.OfType<RadioButton>())
            {
                r.Checked += (_, _) => { ShowChoiceLabels(); popup.IsOpen = false; };
                r.Click += (_, _) => popup.IsOpen = false;       // also when the one already picked is clicked again
            }
        }
        Watch(SizeGroup, SizePopup);
        Watch(CountGroup, CountPopup);
        Watch(QualityGroup, QualityPopup);
        CustomSizeBox.Checked += (_, _) => ShowChoiceLabels();        // the custom size is typed in the popup: it stays open
        CustomSizeBox.Unchecked += (_, _) => ShowChoiceLabels();
        CustomW.TextChanged += (_, _) => ShowChoiceLabels();
        CustomH.TextChanged += (_, _) => ShowChoiceLabels();
        ShowChoiceLabels();
    }

    void ShowChoiceLabels()
    {
        if (SizeChipText == null) return;
        static RadioButton On(Panel g) => g.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        SizeChipText.Text = CustomSizeBox.IsChecked == true ? $"{CustomW.Text} × {CustomH.Text}"
            : On(SizeGroup) is { } s ? AutomationProperties.GetName(s).Split(' ').Last() : "Size";       // "Portrait 2:3" → 2:3
        int n = PickedInt(CountGroup, 1);
        CountChipText.Text = n == 1 ? "1 picture" : $"{n} pictures";
        QualityChipText.Text = On(QualityGroup) is { } q ? AutomationProperties.GetName(q) : "Custom steps";
    }
}
