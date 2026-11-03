using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SSB_GUI.Models;

public class SsbTableInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(Label) ? Id : $"{Id} - {Label}";
}

public class SsbTableListResponse
{
    [JsonPropertyName("tables")]
    public List<SsbTableInfo> Tables { get; set; } = new();
}

public class SsbTableMetadata
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("variables")]
    public List<SsbVariable> Variables { get; set; } = new();
}

public class SsbVariable
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("values")]
    public List<string> Values { get; set; } = new();

    [JsonPropertyName("valueTexts")]
    public List<string> ValueTexts { get; set; } = new();
}

public class SsbValueOption
{
    public string Code { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    public override string ToString() => Label;
}

public class SsbDataResult
{
    public string Title { get; set; } = string.Empty;
    public string SeriesName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public List<string> Labels { get; set; } = new();
    public List<double?> Values { get; set; } = new();
}

public class SsbCachedMetadataItem
{
    public string TableId { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;

    public string DisplayText => string.IsNullOrWhiteSpace(Title) ? $"{TableId} ({Language})" : $"{TableId}: {Title}";

    public override string ToString() => DisplayText;
}