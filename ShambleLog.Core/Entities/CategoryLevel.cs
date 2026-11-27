using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace ShambleLog.Core.Entities;

public class CategoryLevel
{
    /// <summary>
    /// "Default" matches against undefined categories, otherwise we match the most specific defined Category against LogEntry.Category
    /// </summary>
    [Key]
    public string Category { get; set; } = string.Empty;
    public LogLevel Level { get; set; }
    /// <summary>
    /// indicates this was created through the UI and is intended to be safely removable in the UI
    /// </summary>
    public bool IsTemporary { get; set; }
}
