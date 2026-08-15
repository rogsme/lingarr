using System.Text.Json.Serialization;

namespace Lingarr.Contracts.Models.Batch;

/// <summary>
/// A subtitle line passed to a batch translation provider.
/// </summary>
public class BatchSubtitleItem
{
    [JsonPropertyName("position")]
    public int Position { get; set; }

    [JsonPropertyName("line")]
    public string Line { get; set; } = string.Empty;

    /// <summary>
    /// Already-chosen translation for overlap context items; null for lines to translate.
    /// </summary>
    [JsonPropertyName("translation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Translation { get; set; }

    /// <summary>
    /// True for read-only context items from the previous batch that must not be retranslated.
    /// </summary>
    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsContext { get; set; }
}
