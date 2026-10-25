using System.Threading.Tasks;
using System.Windows.Input;

namespace Jarvis.Client.ViewModels;

/// <summary>Admin: the vault's part-numbering convention.</summary>
public sealed partial class MainViewModel
{
    public ICommand NumberingCommand { get; private set; } = null!;

    private void InitializeNumberingCommands()
    {
        NumberingCommand = new RelayCommand(async () => await EditNumberingAsync(), () => CanEdit && IsAdmin);
    }

    private async Task EditNumberingAsync()
    {
        if (_vault == null || _dialogs.AskNumbering(_vault.LoadNumbering()) is not { } answer) return;
        await RunAsync("Saving the part numbering\u2026", async (vault, progress) =>
        {
            await vault.SaveNumberingAsync(answer.Removed ? null : answer.Scheme, progress);
            return answer.Removed ? "Part numbering turned off." : "Part numbering saved. Teammates get it at their next Get Latest.";
        });
    }
}
