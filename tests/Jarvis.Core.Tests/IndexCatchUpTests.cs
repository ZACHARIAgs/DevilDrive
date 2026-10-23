using System;
using System.Collections.Generic;
using System.Linq;
using Jarvis.Core.Vault;
using Xunit;

namespace Jarvis.Core.Tests;

/// <summary>Which files a check-in from SolidWorks also records, so nobody has to rebuild the index.</summary>
public class IndexCatchUpTests
{
    private static readonly DateTimeOffset Monday = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static VaultFile File(string path, IndexEntry? index = null, DateTimeOffset? checkedIn = null, FileState state = FileState.Available) => new VaultFile
    {
        RelativePath = path,
        State = state,
        ExistsLocally = true,
        Index = index,
        LatestVersion = new FileVersion("abc", "Sam", "sam@example.com", checkedIn ?? Monday, "x"),
    };

    private static IndexEntry Record(DateTimeOffset at) => new IndexEntry { UpdatedAt = at };

    [Fact]
    public void Picks_MissingRecords_ThenOutdated_ThenMissingPreviews()
    {
        var files = new[]
        {
            File("up to date.sldprt", Record(Monday.AddSeconds(-5))),           // written with its check-in
            File("no preview.sldasm", Record(Monday)),
            File("outdated.sldprt", Record(Monday), checkedIn: Monday.AddDays(1)), // checked in from the app later
            File("never recorded.sldprt"),
            File("drawing.slddrw", Record(Monday)),                              // drawings have no 3D preview
            File("notes.txt"),                                                    // not a SolidWorks file
        };
        var withPreview = new HashSet<string> { VaultPaths.PreviewFor("up to date.sldprt"), VaultPaths.PreviewFor("outdated.sldprt") };

        var picked = IndexCatchUp.FilesToRecord(files, withPreview.Contains);

        Assert.Equal(new[] { "never recorded.sldprt", "outdated.sldprt", "no preview.sldasm" }, picked.Select(f => f.RelativePath));
    }

    [Fact]
    public void Skips_FilesThatWouldGiveAWrongRecord()
    {
        var files = new[]
        {
            new VaultFile { RelativePath = "edited.sldprt", State = FileState.CheckedOutByMe, ExistsLocally = true, HasLocalChanges = true },
            new VaultFile { RelativePath = "placeholder.sldprt", State = FileState.Available, ExistsLocally = true, NotDownloaded = true },
            new VaultFile { RelativePath = "behind.sldprt", State = FileState.Available, ExistsLocally = true, IsOutdated = true },
            new VaultFile { RelativePath = "new.sldprt", State = FileState.NotInVault, ExistsLocally = true },
            new VaultFile { RelativePath = "gone.sldprt", State = FileState.MissingLocally },
            new VaultFile { RelativePath = "Frame/_versions/bracket v3.sldprt", State = FileState.Available, ExistsLocally = true },
            new VaultFile { RelativePath = "tried.sldprt", State = FileState.Available, ExistsLocally = true },
            new VaultFile { RelativePath = "someone elses.sldprt", State = FileState.CheckedOutByOther, ExistsLocally = true },
        };

        var picked = IndexCatchUp.FilesToRecord(files, _ => false, skip: new HashSet<string> { "tried.sldprt" });

        Assert.Equal(new[] { "someone elses.sldprt" }, picked.Select(f => f.RelativePath)); // its vault copy is unchanged here
    }

    [Fact]
    public void Batch_RoundTripsAsText()
    {
        var batch = new IndexBatch();
        batch.Entries["Frame/bracket.sldprt"] = new IndexEntry { SavedIn = 2026, References = new List<string> { "Frame/bolt.sldprt" } };
        batch.Entries["Frame/bracket.sldprt"].Properties["PartNumber"] = "TLT-101";
        batch.Previews.Add(VaultPaths.PreviewFor("Frame/bracket.sldprt"));

        var back = IndexBatch.Parse(batch.Serialize())!;

        var entry = back.Entries["frame/BRACKET.sldprt"]; // paths are matched without regard to case
        Assert.Equal(2026, entry.SavedIn);
        Assert.Equal("TLT-101", entry.Properties["partnumber"]);
        Assert.Equal(new[] { "Frame/bolt.sldprt" }, entry.References);
        Assert.Equal(new[] { ".pdm/preview/Frame/bracket.sldprt.stl" }, back.Previews);
        Assert.Null(IndexBatch.Parse(""));
        Assert.Null(IndexBatch.Parse("not json"));
    }
}
