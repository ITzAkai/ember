// Prompt editor tools: Find / Replace for both prompt boxes (every match highlighted), adjustable prompt text size, and a negative
// prompt that locks itself when it can't have any effect (at CFG 1 ComfyUI skips the negative pass).
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageForge;

// semi-transparent boxes over every match in a TextBox; the current one stronger
class MatchAdorner : Adorner
{
    readonly TextBox box;
    public List<(int Start, int Length)> Matches = new();
    public int Current = -1;
    public MatchAdorner(TextBox box) : base(box) { this.box = box; IsHitTestVisible = false; box.TextChanged += (_, _) => InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        var fill = new SolidColorBrush(Color.FromArgb(70, 224, 138, 46));
        var strong = new SolidColorBrush(Color.FromArgb(150, 224, 138, 46));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, box.ActualWidth, box.ActualHeight)));
        for (int i = 0; i < Matches.Count; i++)
        {
            var (start, length) = Matches[i];
            if (start + length > box.Text.Length) continue;
            for (int c = start; c < start + length; c++)        // per character, so matches that wrap across lines still show
            {
                var a = box.GetRectFromCharacterIndex(c);
                var b = box.GetRectFromCharacterIndex(c, true);
                if (a.IsEmpty || b.IsEmpty) continue;
                dc.DrawRectangle(i == Current ? strong : fill, null, new Rect(a.TopLeft, new Point(Math.Max(b.Left, a.Left + 1), a.Bottom)));
            }
        }
        dc.Pop();
    }
}

public partial class MainWindow
{
    TextBox findTarget;
    MatchAdorner findMarks;
    readonly Dictionary<TextBox, MatchAdorner> findAdorners = new();

    // ---------------- Find / Replace ----------------
    void FindPrompt_Click(object sender, RoutedEventArgs e) => OpenFind(PromptBox);
    void FindNegative_Click(object sender, RoutedEventArgs e) => OpenFind(NegativeBox);

    void OpenFind(TextBox target)
    {
        CloseFind();
        findTarget = target;
        if (!findAdorners.TryGetValue(target, out findMarks))
        {
            findAdorners[target] = findMarks = new MatchAdorner(target);
            AdornerLayer.GetAdornerLayer(target)?.Add(findMarks);
        }
        target.ScrollToHome();
        FindPopup.PlacementTarget = target;
        FindPopup.IsOpen = true;
        if (target.SelectionLength > 0 && !target.SelectedText.Contains('\n')) FindBox.Text = target.SelectedText;
        FindBox.Focus();
        FindBox.SelectAll();
        UpdateMatches();
    }

    void CloseFind()
    {
        FindPopup.IsOpen = false;
        if (findMarks != null) { findMarks.Matches.Clear(); findMarks.InvalidateVisual(); }
    }

    List<(int Start, int Length)> Matches()
    {
        if (findTarget == null || FindBox.Text == "") return new();
        var opts = MatchCaseBox.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
        return Regex.Matches(findTarget.Text, Regex.Escape(FindBox.Text), opts).Select(m => (m.Index, m.Length)).ToList();
    }

    void UpdateMatches(int current = -1)
    {
        if (findMarks == null) return;
        var all = Matches();
        if (current < 0) current = all.Count == 0 ? -1 : Math.Max(0, all.FindIndex(m => m.Start >= findTarget.CaretIndex));
        if (current >= all.Count) current = all.Count - 1;
        findMarks.Matches = all;
        findMarks.Current = current;
        findMarks.InvalidateVisual();
        FindCount.Text = all.Count == 0 ? (FindBox.Text == "" ? "" : "No matches") : $"{current + 1} of {all.Count}";
        if (current >= 0)
        {
            findTarget.Select(all[current].Start, all[current].Length);
            var line = findTarget.GetLineIndexFromCharacterIndex(all[current].Start);
            if (line >= 0) findTarget.ScrollToLine(line);
        }
    }

    void FindStep(int direction)
    {
        var all = Matches();
        if (all.Count == 0) { UpdateMatches(); return; }
        int now = findMarks?.Current ?? -1;
        UpdateMatches(((now < 0 ? 0 : now + direction) % all.Count + all.Count) % all.Count);
    }

    void FindNext_Click(object sender, RoutedEventArgs e) => FindStep(1);
    void FindPrev_Click(object sender, RoutedEventArgs e) => FindStep(-1);
    void FindClose_Click(object sender, RoutedEventArgs e) { CloseFind(); findTarget?.Focus(); }
    void FindBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateMatches();
    void MatchCase_Click(object sender, RoutedEventArgs e) => UpdateMatches();

    void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.F3) { FindStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
        else if (e.Key == Key.Escape) { FindClose_Click(null, null); e.Handled = true; }
    }

    void Replace_Click(object sender, RoutedEventArgs e)
    {
        var all = Matches();
        int i = findMarks?.Current ?? -1;
        if (i < 0 || i >= all.Count || findTarget.IsReadOnly) return;
        findTarget.Text = findTarget.Text.Remove(all[i].Start, all[i].Length).Insert(all[i].Start, ReplaceBox.Text);
        findTarget.CaretIndex = all[i].Start + ReplaceBox.Text.Length;
        UpdateMatches();
    }

    void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (findTarget == null || FindBox.Text == "" || findTarget.IsReadOnly) return;
        int n = Matches().Count;
        var opts = MatchCaseBox.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
        findTarget.Text = Regex.Replace(findTarget.Text, Regex.Escape(FindBox.Text), ReplaceBox.Text.Replace("$", "$$"), opts);
        UpdateMatches();
        FindCount.Text = $"Replaced {n}";
    }

    // Ctrl+F in either prompt box; F3 / Shift+F3 while the bar is open
    void PromptBoxes_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.F) { OpenFind(box); e.Handled = true; }
        else if (e.Key == Key.F3 && FindPopup.IsOpen) { FindStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { PromptSize(+1); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { PromptSize(-1); e.Handled = true; }
        else if (e.Key == Key.Escape && FindPopup.IsOpen) { CloseFind(); e.Handled = true; }
    }

    // ---------------- text size ----------------
    void PromptBoxes_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        PromptSize(Math.Sign(e.Delta));
        e.Handled = true;
    }

    void SmallerText_Click(object sender, RoutedEventArgs e) => PromptSize(-1);
    void BiggerText_Click(object sender, RoutedEventArgs e) => PromptSize(+1);

    void PromptSize(int direction)
    {
        settings.PromptTextSize = Math.Clamp(settings.PromptTextSize + direction, 11, 26);
        ApplyPromptSize();
        settings.Save();
    }

    void ApplyPromptSize()
    {
        double size = settings.PromptTextSize;
        PromptBox.FontSize = NegativeBox.FontSize = size;
        PromptBox.Height = Math.Round(140 * size / 14.5);      // more lines show as the text grows
        NegativeBox.Height = Math.Round(66 * size / 14.5);
        foreach (var m in findAdorners.Values) m.InvalidateVisual();
    }

    // ---------------- the negative prompt ----------------
    bool negativeUnlocked;

    // locked (contents kept) while it can't change anything; unlocked and filled with the model's own negative where it does
    void UpdateNegative()
    {
        if (NegativeBox == null || ModelBox.SelectedItem == null) return;
        bool ignored = !NegativeWorks;
        bool locked = ignored && !negativeUnlocked;
        NegativeBox.IsReadOnly = locked;
        NegativeBox.Opacity = locked ? 0.55 : 1;
        NegativeLock.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        NegativeWarn.Visibility = ignored && negativeUnlocked ? Visibility.Visible : Visibility.Collapsed;
        NegativeWarn.Text = NagState().Can ? "Negative prompt has no effect at CFG 1. Turn on NAG under Advanced settings > Quality boosters to use it (each step takes about twice as long)."
            : "Negative prompt has no effect at CFG 1. Raise CFG to use it (slower, and it may over-cook turbo models).";
        if (!ignored && string.IsNullOrWhiteSpace(NegativeBox.Text) && ModelRec is { Negative: true, DefaultNegative: { Length: > 0 } neg })
            NegativeBox.Text = neg;
    }

    void UnlockNegative_Click(object sender, RoutedEventArgs e) { negativeUnlocked = true; UpdateNegative(); NegativeBox.Focus(); }
}
