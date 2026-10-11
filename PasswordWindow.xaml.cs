// The secret gallery's password: a new one (typed twice), or the one that opens it.
// check runs off the UI thread and returns an error to show, or null when the password is good.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ImageForge;

public partial class PasswordWindow : Window
{
    readonly bool create;
    readonly Func<string, string> check;
    bool busy;

    public PasswordWindow(bool create, Func<string, string> check)
    {
        InitializeComponent();
        (this.create, this.check) = (create, check);
        TitleText.Text = create ? "Lock your secret gallery" : "Secret gallery";
        SubText.Text = create ? "Choose a password. Pictures you hide here are encrypted with it." : "Type your password to open it.";
        OkBtn.Content = create ? "Create password" : "Unlock";
        AgainPanel.Visibility = Warning.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => { First.Focus(); ShowCaps(); };
        PreviewKeyUp += (_, _) => ShowCaps();
        Closing += (_, e) => e.Cancel = busy;          // not while the key is being made
    }

    static string Typed(PasswordBox p, TextBox t) => t.Visibility == Visibility.Visible ? t.Text : p.Password;
    Control FirstField => FirstShown.Visibility == Visibility.Visible ? FirstShown : First;

    void ShowCaps() => CapsNote.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        bool show = FirstShown.Visibility != Visibility.Visible;
        foreach (var (p, t) in new[] { (First, FirstShown), (Again, AgainShown) })
        {
            if (show) t.Text = p.Password; else p.Password = t.Text;
            p.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            t.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        RevealBtn.Content = show ? "" : "";
        RevealBtn.ToolTip = show ? "Hide password" : "Show password";
        FirstShown.CaretIndex = FirstShown.Text.Length;
        FirstField.Focus();
    }

    async void Ok_Click(object sender, RoutedEventArgs e)
    {
        string pw = Typed(First, FirstShown);
        string error = pw.Length == 0 ? "Type a password."
            : create && pw.Length < 4 ? "Use at least 4 characters."
            : create && pw != Typed(Again, AgainShown) ? "The two passwords don't match."
            : null;
        if (error == null)
        {
            busy = true;
            Buttons.IsEnabled = false;
            OkBtn.Content = create ? "Creating…" : "Unlocking…";
            try { error = await Task.Run(() => check(pw)); }
            catch (Exception ex) { error = "Couldn't open the secret gallery: " + ex.Message; }
            busy = false;
            Buttons.IsEnabled = true;
            OkBtn.Content = create ? "Create password" : "Unlock";
            if (error == null) { DialogResult = true; return; }
        }
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        if (!create) { First.Clear(); FirstShown.Clear(); }
        FirstField.Focus();
    }

    void Card_MouseDown(object sender, MouseButtonEventArgs e) => DragMove();
}
