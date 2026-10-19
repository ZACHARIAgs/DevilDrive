using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace CapstonePdm.Client.Views;

/// <summary>Collects the required check-in comment. Also used by History for restores.</summary>
public partial class CheckInWindow : Window
{
    public CheckInWindow(string title, string okText, IReadOnlyList<string> files, string defaultComment, bool allowKeepCheckedOut, string? prompt = null)
    {
        InitializeComponent();
        Title = title;
        if (prompt != null) PromptText.Text = prompt;
        OkButton.Content = okText;
        FilesHeader.Text = files.Count == 1 ? "File:" : $"Files ({files.Count}):";
        FileList.ItemsSource = files;
        CommentBox.Text = defaultComment;
        KeepCheckedOutBox.Visibility = allowKeepCheckedOut ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            CommentBox.Focus();
            CommentBox.SelectAll();
        };
    }

    public string Comment => CommentBox.Text.Trim();
    public bool KeepCheckedOut => KeepCheckedOutBox.IsChecked == true;

    private void OnCommentChanged(object sender, TextChangedEventArgs e) =>
        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(CommentBox.Text);

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
