using System.ComponentModel.DataAnnotations;

namespace ShambleLog.Core.Entities;

public class EFCoreEntry
{
    /// <summary>
    /// Primary key and foreign key to <see cref="LogEntry.Id"/> (one-to-one)
    /// </summary>
    [Key]
    public long LogEntryId { get; set; }
    public LogEntry LogEntry { get; set; } = null!;
    /// <summary>
    /// Table being updated, inserted into, deleted from, or selected from.
    /// Multiple tables (e.g. with joins) will be concatenated together with a comma separator
    /// </summary>
    public string[] TargetTables { get; set; } = [];
    /// <summary>
    /// tags extracted from the query, which were added on client side using TagWith
    /// </summary>
    public string[] Tags { get; set; } = [];
    public string CommandText { get; set; } = string.Empty;
    public string? Parameters { get; set; }
    public long ElapsedMs { get; set; }
}
