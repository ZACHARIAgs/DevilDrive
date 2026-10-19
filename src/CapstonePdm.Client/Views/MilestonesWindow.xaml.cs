using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CapstonePdm.Client.ViewModels;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Vault;

namespace CapstonePdm.Client.Views;

public partial class MilestonesWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly IDialogs _dialogs;

    public MilestonesWindow(MainViewModel vm, IDialogs dialogs)
    {
        InitializeComponent();
        _vm = vm;
        _dialogs = dialogs;
        CreatePanel.Visibility = vm.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        StatusText.Text = "Loading…";
        try
        {
            var milestones = await _vm.LoadMilestonesAsync();
            MilestonesGrid.ItemsSource = milestones;
            StatusText.Text = milestones.Count == 0
                ? (_vm.IsAdmin ? "No milestones yet. Create one above, e.g. before a design review." : "No milestones yet. An admin creates them.")
                : $"{milestones.Count} milestone(s), newest first.";
        }
        catch (PdmException ex)
        {
            StatusText.Text = "Couldn't load milestones.";
            _dialogs.ShowError(ex);
        }
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) =>
        CreateButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        StatusText.Text = "Creating…";
        if (await _vm.CreateMilestoneAsync(NameBox.Text, DescriptionBox.Text))
        {
            NameBox.Clear();
            DescriptionBox.Clear();
        }
        IsEnabled = true;
        await LoadAsync();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewButton.IsEnabled = MilestonesGrid.SelectedItem is Milestone;

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MilestonesGrid.SelectedItem is Milestone) OnView(sender, e);
    }

    private async void OnView(object sender, RoutedEventArgs e)
    {
        if (MilestonesGrid.SelectedItem is not Milestone milestone) return;
        if (!_dialogs.Confirm("View Milestone",
                $"Show every file as it was at “{milestone.Name}”?\n\n" +
                "Your vault folder switches to those versions, read-only. Close any vault files open in SolidWorks first. " +
                "Click Return to Latest when you're done."))
        {
            return;
        }
        Close();
        await _vm.ViewMilestoneAsync(milestone);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
