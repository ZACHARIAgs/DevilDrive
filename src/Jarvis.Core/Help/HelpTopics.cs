using System;
using System.Collections.Generic;
using System.Linq;

namespace Jarvis.Core.Help;

/// <summary>One common problem: what you see, and what to do about it.</summary>
public sealed class HelpTopic
{
    public HelpTopic(string title, string seen, params string[] steps)
    {
        Title = title;
        Seen = seen;
        Steps = steps;
    }

    /// <summary>The message or state as it appears in Jarvis.</summary>
    public string Title { get; }
    /// <summary>What it means.</summary>
    public string Seen { get; }
    /// <summary>What to do, in order.</summary>
    public IReadOnlyList<string> Steps { get; }

    /// <summary>Whether every word typed appears somewhere in the topic.</summary>
    public bool Matches(string? query) =>
        string.IsNullOrWhiteSpace(query) ||
        query!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .All(word => (Title + " " + Seen + " " + string.Join(" ", Steps)).IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);

    public override string ToString() =>
        Title + "\n" + Seen + "\n" + string.Join("\n", Steps.Select((s, i) => $"{i + 1}. {s}"));
}

/// <summary>
/// The Help window's content (the app's Help button and the SolidWorks pane's): the problems people run into most,
/// in the words Jarvis shows them, each with the steps that fix it.
/// </summary>
public static class HelpTopics
{
    public const string BackupsNote = "DevilDrive keeps a copy of every file it replaces or deletes in %LocalAppData%\\DevilDrive\\Backups, in a folder named by date and time, for 60 days.";

    public static IReadOnlyList<HelpTopic> All { get; } = new[]
    {
        new HelpTopic("\"Edited WITHOUT check-out!\" (orange)",
            "You changed a file you hadn't checked out (or SolidWorks saved it). Get Latest won't replace it, so your work isn't lost.",
            "To keep your changes: select the file and click Check Out (your edits stay), then Check In.",
            "To throw them away: right-click it and choose Undo Check Out. It works even though you never checked it out: it puts the vault's version back.",
            "A copy of your version is kept in the Backups folder either way. Next time, check out before editing."),

        new HelpTopic("Conflict: \"These files have a newer version in the vault, but you also changed them on this PC\"",
            "Get Latest stopped because someone else changed files you also changed. Nothing was overwritten.",
            "Close SolidWorks.",
            "In the DevilDrive app, the message has a button: Keep copies, get the vault's. It does the steps below in one go.",
            "By hand (or from the SolidWorks pane):",
            "Set the filter (top right) to Needs attention. The files in the message show as orange \"Edited WITHOUT check-out!\".",
            "Select all of them: click one, press Ctrl+A, and scroll to the bottom to make sure every one is selected.",
            "Right-click, choose Undo Check Out and confirm. Your versions are kept in the Backups folder.",
            "Click Get Latest.",
            "If you really changed some of them, take your version from the Backups folder, check out the vault's file, copy yours over it and check it in."),

        new HelpTopic("\"Get Latest didn't finish\" or \"couldn't be downloaded\"",
            "Something else in the same message stopped Get Latest part way, so newer files weren't downloaded yet.",
            "Read the other messages in the window (often a Conflict or a file in use) and fix that first.",
            "Then click Get Latest again."),

        new HelpTopic("File in use: \"Close it in SolidWorks\"",
            "DevilDrive needs to replace or change a file that's open in SolidWorks (or in an assembly that's open).",
            "Save and close the file, and any assembly or drawing that uses it, in SolidWorks.",
            "Try again. (Checking in works with files open; Get Latest, Undo, Rename and Move need them closed.)"),

        new HelpTopic("\"Checked out by someone\" (red)",
            "Someone else is editing the file, so you can only open it read-only.",
            "Ask them to check it in. Click \"Notify me when it's free\" to be told when they do.",
            "If they're unreachable and the check-out is stale, ask an admin to use Break Lock."),

        new HelpTopic("SolidWorks says the file is read-only, or won't save",
            "Files you haven't checked out are read-only on purpose, so nobody edits without the team knowing.",
            "Close the file without saving, check it out in DevilDrive (or the SolidWorks pane), then open it again.",
            "Don't clear the read-only flag by hand."),

        new HelpTopic("A file I used to open is gone (renamed or moved)",
            "The vault renamed or moved it (for example to its part number, TLT-012). Assemblies were updated to the new name.",
            "Click Get Latest.",
            "Find it in DevilDrive by searching its description, or look in History for the rename.",
            "Open it under the new name from the vault folder. Don't use old copies elsewhere."),

        new HelpTopic("An assembly opens with missing parts",
            "Some parts aren't on this PC yet (\"Available (not on this PC yet)\"), or a file was renamed or moved outside DevilDrive.",
            "Click Get Latest. In the SolidWorks pane, click \"Download missing parts\".",
            "If you renamed or moved a file in File Explorer, move it back and use Rename or Move to Folder in DevilDrive instead: they update every assembly that uses it."),

        new HelpTopic("\"Wrong SolidWorks version\" or a file saved in a newer SolidWorks",
            "The file was last saved in a newer SolidWorks than the team uses, so teammates couldn't open it.",
            "Undo Check Out to get the vault version back, then redo the change in the team's SolidWorks version."),

        new HelpTopic("Frozen (black) or In review (green)",
            "An admin froze the file (released), or it's waiting for review. Nobody can change it meanwhile.",
            "Ask an admin. They can unfreeze it, or approve or reject the review."),

        new HelpTopic("\"Check-in not sent to the vault\" (yellow banner)",
            "Your new version is saved on this PC but didn't reach GitHub (offline, or the vault refused it). The files stay checked out to you.",
            "Fix what the message says (often just being offline).",
            "Click Check In again: it sends what's waiting."),

        new HelpTopic("Vault offline, sign-in or permission problems",
            "No internet, GitHub is down, or your GitHub sign-in expired. Nothing was changed.",
            "Check your internet and try again; DevilDrive keeps showing the last known state.",
            "If it asks you to sign in, sign in to GitHub with the account the vault invitation went to.",
            "If it still fails, ask an admin to check you're on the team (Team button)."),

        new HelpTopic("\"Update Vault Setup\" banner (admins)",
            "The vault's own setup (the vault guard, preview storage, the joining steps on its GitHub page) is missing or older than this DevilDrive.",
            "An admin clicks Update Vault Setup. It only changes DevilDrive's setup files, never your SolidWorks files."),

        new HelpTopic("I lost my changes / where are the backups?",
            BackupsNote,
            "Click Open backups folder (below) to find them.",
            "To use one: check out the vault's file, copy the backup over it (keeping the vault's file name), then check it in."),

        new HelpTopic("Asking a teammate for help",
            "Every error window has a Copy button.",
            "Click Copy and paste the text into your team chat.",
            "If they need more, send them the log: %LocalAppData%\\DevilDrive\\app.log for the app, addin.log (same folder) for the SolidWorks pane."),
    };
}
