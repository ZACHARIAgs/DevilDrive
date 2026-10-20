using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jarvis.Core.Vault;

/// <summary>A labeled snapshot of the whole vault (a Git tag), e.g. "Design Review 1".</summary>
public sealed class Milestone
{
    public Milestone(string tag, string name, string description, string createdBy, DateTimeOffset createdAt)
    {
        Tag = tag;
        Name = name;
        Description = description;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
    }

    /// <summary>Git tag, e.g. milestone/design-review-1.</summary>
    public string Tag { get; }
    public string Name { get; }
    public string Description { get; }
    public string CreatedBy { get; }
    public DateTimeOffset CreatedAt { get; }
}

internal static class MilestoneTags
{
    public const string Prefix = "milestone/";
    private const char RecordSep = '\x1e';
    private const char FieldSep = '\x1f';

    public const string ListFormat = "%(refname:strip=2)%1f%(taggername)%1f%(taggerdate:iso-strict)%1f%(contents)%1e";

    /// <summary>"Design Review 1" becomes milestone/design-review-1.</summary>
    public static string TagFor(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) && c < 128) slug.Append(c);
            else if (slug.Length > 0 && slug[slug.Length - 1] != '-') slug.Append('-');
        }
        var text = slug.ToString().Trim('-');
        return Prefix + (text.Length > 0 ? text : "milestone");
    }

    /// <summary>Parses git for-each-ref output in <see cref="ListFormat"/>. The tag message's first line is the name.</summary>
    public static List<Milestone> Parse(string output)
    {
        var list = new List<Milestone>();
        foreach (var record in output.Split(RecordSep))
        {
            var fields = record.Split(new[] { FieldSep }, 4);
            if (fields.Length < 4) continue;
            var tag = fields[0].Trim();
            if (tag.Length == 0) continue;
            var message = fields[3].Replace("\r", "").Trim();
            var newline = message.IndexOf('\n');
            var name = newline >= 0 ? message.Substring(0, newline).Trim() : message;
            var description = newline >= 0 ? message.Substring(newline + 1).Trim() : "";
            DateTimeOffset.TryParse(fields[2].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at);
            list.Add(new Milestone(tag, name.Length > 0 ? name : tag.Substring(Prefix.Length), description, fields[1].Trim(), at));
        }
        return list;
    }
}
