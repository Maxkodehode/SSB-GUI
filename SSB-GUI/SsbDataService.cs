using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using SSB_GUI.Models;

namespace SSB_GUI;

public class SsbDataService
{
    private readonly HttpClient _httpClient = new();
    
    private readonly string _tocDirectory;
    private readonly string _metadataDirectory;
    private readonly string _dataDirectory;

    public SsbDataService()
    {
        string baseDataDir = ResolveBaseDataDirectory();
        _tocDirectory = Path.Combine(baseDataDir, "table-of-content");
        _metadataDirectory = Path.Combine(baseDataDir, "metadata");
        _dataDirectory = Path.Combine(baseDataDir, "data");

        Directory.CreateDirectory(_tocDirectory);
        Directory.CreateDirectory(_metadataDirectory);
        Directory.CreateDirectory(_dataDirectory);

        SyncDataFilesFromOtherLocations(baseDataDir);
    }

    private static string ResolveBaseDataDirectory()
    {
        string curDir = Directory.GetCurrentDirectory();
        string candidateProject = Path.Combine(curDir, "SSB-GUI", "data-files-JSON");
        if (Directory.Exists(Path.Combine(curDir, "SSB-GUI")) && !curDir.EndsWith("SSB-GUI"))
        {
            return candidateProject;
        }

        return Path.Combine(curDir, "data-files-JSON");
    }

    private static void SyncDataFilesFromOtherLocations(string targetBaseDataDir)
    {
        string curDir = Directory.GetCurrentDirectory();
        string baseDir = AppContext.BaseDirectory;

        var candidateSources = new List<string>
        {
            Path.Combine(baseDir, "data-files-JSON"),
            Path.Combine(curDir, "data-files-JSON"),
            Path.Combine(curDir, "SSB-GUI", "data-files-JSON")
        };

        foreach (var sourceDir in candidateSources)
        {
            if (!Directory.Exists(sourceDir) || string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(targetBaseDataDir), StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var subDir in new[] { "metadata", "table-of-content", "data" })
            {
                string srcSub = Path.Combine(sourceDir, subDir);
                string dstSub = Path.Combine(targetBaseDataDir, subDir);
                if (Directory.Exists(srcSub))
                {
                    Directory.CreateDirectory(dstSub);
                    foreach (var file in Directory.GetFiles(srcSub, "*.json"))
                    {
                        string dstFile = Path.Combine(dstSub, Path.GetFileName(file));
                        if (!File.Exists(dstFile))
                        {
                            try
                            {
                                File.Copy(file, dstFile, overwrite: false);
                            }
                            catch
                            {
                                // ignore
                            }
                        }
                    }
                }
            }
        }
    }

    private string GetFilePathForLanguage(string lang) =>
        Path.Combine(_tocDirectory, $"table_of_contents_{lang}.json");

    public async Task<SsbDataResult?> FetchTableDataAsync(string tableId, Dictionary<string, string> selectedParameters, string lang = "no")
    {
        Directory.CreateDirectory(_dataDirectory);
        try
        {
            var queryList = new List<object>();
            foreach (var (code, val) in selectedParameters)
            {
                if (val == "*")
                {
                    queryList.Add(new
                    {
                        code = code,
                        selection = new
                        {
                            filter = "all",
                            values = new[] { "*" }
                        }
                    });
                }
                else
                {
                    queryList.Add(new
                    {
                        code = code,
                        selection = new
                        {
                            filter = "item",
                            values = new[] { val }
                        }
                    });
                }
            }

            var payload = new
            {
                query = queryList,
                response = new
                {
                    format = "json-stat2"
                }
            };

            string requestJson = JsonSerializer.Serialize(payload);
            using var requestContent = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

            string queryUrl = $"https://data.ssb.no/api/v0/{lang}/table/{tableId}";
            var response = await _httpClient.PostAsync(queryUrl, requestContent);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[SSB Error] API query failed ({response.StatusCode}): {errorBody}");
                return null;
            }

            string rawResponse = await response.Content.ReadAsStringAsync();

            string fileName = $"{tableId}_{lang}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
            string filePath = Path.Combine(_dataDirectory, fileName);
            using (var jsonDoc = JsonDocument.Parse(rawResponse))
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string formatted = JsonSerializer.Serialize(jsonDoc.RootElement, options);
                await File.WriteAllTextAsync(filePath, formatted);
            }

            return ParseJsonStat2Result(rawResponse, tableId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SSB Error] Failed to fetch table data for {tableId}: {ex.Message}");
            return null;
        }
    }

    private SsbDataResult ParseJsonStat2Result(string json, string tableId)
    {
        var result = new SsbDataResult();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        result.Title = root.TryGetProperty("label", out var labelEl) ? labelEl.GetString() ?? tableId : tableId;

        if (root.TryGetProperty("value", out var valEl) && valEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in valEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out double d))
                {
                    result.Values.Add(d);
                }
                else
                {
                    result.Values.Add(null);
                }
            }
        }

        if (root.TryGetProperty("dimension", out var dimEl) && root.TryGetProperty("id", out var idEl))
        {
            string? multiValueDim = null;
            int maxCount = 1;

            string? timeDimCode = null;
            if (root.TryGetProperty("role", out var roleEl) && 
                roleEl.TryGetProperty("time", out var timeRoleEl) && 
                timeRoleEl.ValueKind == JsonValueKind.Array &&
                timeRoleEl.GetArrayLength() > 0)
            {
                timeDimCode = timeRoleEl[0].GetString();
            }

            foreach (var dCode in idEl.EnumerateArray())
            {
                string? code = dCode.GetString();
                if (code != null && dimEl.TryGetProperty(code, out var dimObj))
                {
                    if (dimObj.TryGetProperty("category", out var catObj) && catObj.TryGetProperty("label", out var catLabelObj))
                    {
                        int count = 0;
                        foreach (var _ in catLabelObj.EnumerateObject()) count++;
                        
                        if (code == timeDimCode && count > 1)
                        {
                            multiValueDim = code;
                            maxCount = count;
                        }
                        else if (count > maxCount)
                        {
                            maxCount = count;
                            multiValueDim = code;
                        }
                    }

                    if (dimObj.TryGetProperty("category", out var cat) && cat.TryGetProperty("unit", out var unitObj))
                    {
                        foreach (var u in unitObj.EnumerateObject())
                        {
                            if (u.Value.TryGetProperty("base", out var baseEl))
                            {
                                result.Unit = baseEl.GetString() ?? "";
                            }
                        }
                    }
                }
            }

            if (multiValueDim != null && dimEl.TryGetProperty(multiValueDim, out var targetDim))
            {
                if (targetDim.TryGetProperty("category", out var catObj) && catObj.TryGetProperty("label", out var catLabelObj))
                {
                    foreach (var prop in catLabelObj.EnumerateObject())
                    {
                        result.Labels.Add(prop.Value.GetString() ?? prop.Name);
                    }
                }
            }
        }

        if (result.Labels.Count == 0)
        {
            for (int i = 0; i < result.Values.Count; i++)
            {
                result.Labels.Add($"#{i + 1}");
            }
        }

        return result;
    }

    public async Task<List<SsbCachedMetadataItem>> GetCollectedMetadataListAsync(string? filterLang = null)
    {
        Directory.CreateDirectory(_metadataDirectory);
        var list = new List<SsbCachedMetadataItem>();

        var files = Directory.GetFiles(_metadataDirectory, "*.json");
        foreach (var file in files)
        {
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
            string tableId = fileNameWithoutExt;
            string lang = "no";
            int lastUnderscore = fileNameWithoutExt.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                string suffix = fileNameWithoutExt[(lastUnderscore + 1)..];
                if (suffix.Equals("no", StringComparison.OrdinalIgnoreCase) || suffix.Equals("en", StringComparison.OrdinalIgnoreCase))
                {
                    tableId = fileNameWithoutExt[..lastUnderscore];
                    lang = suffix.ToLowerInvariant();
                }
            }

            if (!string.IsNullOrEmpty(filterLang) && !lang.Equals(filterLang, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string title = tableId;
            try
            {
                string json = await File.ReadAllTextAsync(file);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("title", out var titleEl))
                {
                    title = titleEl.GetString() ?? tableId;
                }
            }
            catch
            {
            }

            list.Add(new SsbCachedMetadataItem
            {
                TableId = tableId,
                Language = lang,
                Title = title,
                FilePath = file
            });
        }

        list.Sort((a, b) => string.Compare(a.DisplayText, b.DisplayText, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public async Task<SsbTableMetadata?> GetTableMetadataAsync(string tableId, string lang = "no", bool forceFetch = false)
    {
        Directory.CreateDirectory(_metadataDirectory);
        string filePath = Path.Combine(_metadataDirectory, $"{tableId}_{lang}.json");

        if (!File.Exists(filePath) && !forceFetch)
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "data-files-JSON", "metadata", $"{tableId}_{lang}.json");
            if (File.Exists(fallback))
            {
                try
                {
                    File.Copy(fallback, filePath, overwrite: false);
                }
                catch
                {
                }
            }
        }

        if (!File.Exists(filePath) || forceFetch)
        {
            try
            {
                string metadataUrl = $"https://data.ssb.no/api/v0/{lang}/table/{tableId}";
                var rawJson = await _httpClient.GetStringAsync(metadataUrl);

                using var jsonDoc = JsonDocument.Parse(rawJson);
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string formattedJson = JsonSerializer.Serialize(jsonDoc.RootElement, options);
                await File.WriteAllTextAsync(filePath, formattedJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SSB Error] Failed to fetch metadata for table {tableId}: {ex.Message}");
                return null;
            }
        }

        try
        {
            string json = await File.ReadAllTextAsync(filePath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<SsbTableMetadata>(json, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SSB Error] Failed to parse metadata for table {tableId}: {ex.Message}");
            return null;
        }
    }

    public async Task<List<SsbTableInfo>> GetTablesAsync(string lang = "no")
    {
        await EnsureTableOfContentsAsync(lang);

        string filePath = GetFilePathForLanguage(lang);
        if (!File.Exists(filePath))
            return new List<SsbTableInfo>();

        try
        {
            string json = await File.ReadAllTextAsync(filePath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var response = JsonSerializer.Deserialize<SsbTableListResponse>(json, options);
            return response?.Tables ?? new List<SsbTableInfo>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SSB Error] Failed to parse JSON for lang '{lang}': {ex.Message}");
            return new List<SsbTableInfo>();
        }
    }

    public async Task EnsureTableOfContentsAsync(string lang = "no")
    {
        Directory.CreateDirectory(_tocDirectory);
        string filePath = GetFilePathForLanguage(lang);

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"[SSB] Table of contents ({lang}) not found. Fetching from ssb.no...");
            await FetchAndSaveTableOfContentsAsync(lang, filePath);
        }
    }

    private async Task FetchAndSaveTableOfContentsAsync(string lang, string destinationPath)
    {
        try
        {
            string ssbUrl = $"https://data.ssb.no/api/pxwebapi/v2/tables?lang={lang}";

            var rawJson = await _httpClient.GetStringAsync(ssbUrl);

            using var jsonDoc = JsonDocument.Parse(rawJson);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string formattedJson = JsonSerializer.Serialize(jsonDoc.RootElement, options);

            await File.WriteAllTextAsync(destinationPath, formattedJson);
            Console.WriteLine($"[SSB] Table of contents ({lang}) saved successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SSB Error] Failed to fetch table of contents ({lang}): {ex.Message}");
        }
    }
}