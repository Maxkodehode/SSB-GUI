using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SSB_GUI.Models;

namespace SSB_GUI;

public partial class MainWindow : Window
{
    private readonly SsbDataService _ssbDataService = new();
    
    private readonly Dictionary<string, Control> _parameterControls = new();

    private SsbCachedMetadataItem? _currentMetadataItem;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureTableSearchFilter();
        Loaded += OnWindowLoaded;
    }

    private void ConfigureTableSearchFilter()
    {
        TableSearchBox.ItemFilter = (searchText, item) =>
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return true;

            if (item is SsbTableInfo table)
            {
                return table.Label.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                       table.Id.Contains(searchText, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        };
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        await LoadTablesForSelectedLanguageAsync();
        await RefreshCollectedMetadataListAsync();
    }

    private async void OnLanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox)
            return;

        if (comboBox.SelectedItem == null)
        {
            comboBox.SelectedIndex = 0;
            return;
        }

        if (IsLoaded)
        {
            ClearDynamicParameters();
            ResetChartArea();
            await LoadTablesForSelectedLanguageAsync();
            await RefreshCollectedMetadataListAsync();
        }
    }

    private async Task LoadTablesForSelectedLanguageAsync()
    {
        string selectedLang = GetSelectedLanguageCode();

        if (TableSearchBox != null)
        {
            TableSearchBox.Text = string.Empty;
            TableSearchBox.SelectedItem = null;
            
            var tables = await _ssbDataService.GetTablesAsync(selectedLang);
            TableSearchBox.ItemsSource = tables;
        }

        if (FetchMetadataButton != null)
        {
            FetchMetadataButton.IsEnabled = false;
        }
    }

    private string GetSelectedLanguageCode()
    {
        if (LanguageComboBox?.SelectedItem is ComboBoxItem item && item.Tag is string langCode)
            return langCode;
        return "no";
    }

    private void OnTableSearchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FetchMetadataButton != null)
        {
            FetchMetadataButton.IsEnabled = TableSearchBox?.SelectedItem is SsbTableInfo;
        }
    }

    private async void OnFetchMetadataClicked(object? sender, RoutedEventArgs e)
    {
        if (TableSearchBox?.SelectedItem is not SsbTableInfo selectedTable)
            return;

        string lang = GetSelectedLanguageCode();
        if (FetchMetadataButton != null)
            FetchMetadataButton.IsEnabled = false;

        try
        {
            var metadata = await _ssbDataService.GetTableMetadataAsync(selectedTable.Id, lang, forceFetch: true);
            if (metadata != null)
            {
                await RefreshCollectedMetadataListAsync(selectTableId: selectedTable.Id);
            }
        }
        finally
        {
            if (FetchMetadataButton != null)
                FetchMetadataButton.IsEnabled = TableSearchBox?.SelectedItem is SsbTableInfo;
        }
    }

    private async Task RefreshCollectedMetadataListAsync(string? selectTableId = null)
    {
        string lang = GetSelectedLanguageCode();
        var metadataList = await _ssbDataService.GetCollectedMetadataListAsync(lang);

        if (MetadataComboBox != null)
        {
            MetadataComboBox.ItemsSource = metadataList;

            if (!string.IsNullOrEmpty(selectTableId))
            {
                var match = metadataList.FirstOrDefault(m => m.TableId.Equals(selectTableId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    MetadataComboBox.SelectedItem = match;
                    return;
                }
            }

            if (MetadataComboBox.SelectedItem is SsbCachedMetadataItem currentSelected)
            {
                var match = metadataList.FirstOrDefault(m => m.TableId.Equals(currentSelected.TableId, StringComparison.OrdinalIgnoreCase));
                MetadataComboBox.SelectedItem = match;
            }
        }
    }

    private async void OnMetadataSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ClearDynamicParameters();
        ResetChartArea();

        if (MetadataComboBox?.SelectedItem is SsbCachedMetadataItem metadataItem)
        {
            _currentMetadataItem = metadataItem;
            Console.WriteLine($"[SSB] Selected Metadata: {metadataItem.TableId} ({metadataItem.Language}) - {metadataItem.Title}");
            await BuildDynamicParametersForTableAsync(metadataItem.TableId, metadataItem.Language);
        }
        else
        {
            _currentMetadataItem = null;
        }
    }

    private async Task BuildDynamicParametersForTableAsync(string tableId, string lang)
    {
        var metadata = await _ssbDataService.GetTableMetadataAsync(tableId, lang);

        if (metadata == null || metadata.Variables.Count == 0)
        {
            DynamicParametersPanel.Children.Add(new TextBlock 
            { 
                Text = "No variable metadata found for this table.", 
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }

        // Dynamically create a labeled dropdown/control for every variable in the table
        foreach (var variable in metadata.Variables)
        {
            var options = new List<SsbValueOption>();

            // If time dimension or large set of values, provide an "All periods" option
            bool isTime = variable.Code.Equals("Tid", StringComparison.OrdinalIgnoreCase) ||
                          variable.Text.Contains("tid", StringComparison.OrdinalIgnoreCase) ||
                          variable.Text.Contains("år", StringComparison.OrdinalIgnoreCase) ||
                          variable.Text.Contains("year", StringComparison.OrdinalIgnoreCase);

            if (isTime && variable.Values.Count > 1)
            {
                options.Add(new SsbValueOption
                {
                    Code = "*",
                    Label = lang == "en" ? "All periods (Time series)" : "Hele perioden (Tidsserie)"
                });
            }

            for (int i = 0; i < variable.Values.Count; i++)
            {
                string code = variable.Values[i];
                string label = i < variable.ValueTexts.Count ? variable.ValueTexts[i] : code;
                options.Add(new SsbValueOption { Code = code, Label = label });
            }

            var container = new StackPanel 
            { 
                Margin = new Avalonia.Thickness(0, 0, 16, 8),
                Spacing = 4
            };

            // Label for the parameter (e.g. "Region", "Kjønn", "Tid")
            container.Children.Add(new TextBlock 
            { 
                Text = !string.IsNullOrEmpty(variable.Text) 
                    ? char.ToUpper(variable.Text[0]) + variable.Text[1..] 
                    : variable.Code,
                FontWeight = FontWeight.Medium,
                FontSize = 12
            });

            // Create dropdown for the variable
            var comboBox = new ComboBox 
            { 
                Width = 200,
                ItemsSource = options,
                PlaceholderText = $"Select {variable.Text}..."
            };

            // If time dimension with "All", pre-select it
            if (isTime && options.Count > 0)
            {
                comboBox.SelectedIndex = 0;
            }

            comboBox.SelectionChanged += (_, _) => UpdateFetchButtonState();

            container.Children.Add(comboBox);
            DynamicParametersPanel.Children.Add(container);

            _parameterControls[variable.Code] = comboBox;
        }

        UpdateFetchButtonState();
    }

    private void UpdateFetchButtonState()
    {
        if (FetchButton == null)
            return;

        // Fetch button is enabled only when all required dynamic dropdowns have a selection and a metadata item is selected
        bool allSelected = _currentMetadataItem != null &&
                           _parameterControls.Count > 0 &&
                           _parameterControls.Values.All(c => c is ComboBox cb && cb.SelectedItem != null);

        FetchButton.IsEnabled = allSelected;
    }

    private void ClearDynamicParameters()
    {
        _parameterControls.Clear();
        DynamicParametersPanel?.Children.Clear();
        if (FetchButton != null)
            FetchButton.IsEnabled = false;
    }

    private void ResetChartArea()
    {
        if (ChartControl != null)
        {
            ChartControl.Series = Array.Empty<ISeries>();
            ChartControl.IsVisible = false;
        }

        if (ChartTitleTextBlock != null)
        {
            ChartTitleTextBlock.Text = string.Empty;
            ChartTitleTextBlock.IsVisible = false;
        }

        if (ChartPlaceholder != null)
        {
            ChartPlaceholder.Text = "Select a metadata file and query parameters above, then click 'Fetch Data' to display the graph.";
            ChartPlaceholder.IsVisible = true;
        }
    }

    private async void OnFetchDataClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentMetadataItem == null)
            return;

        // Gather selected parameters to build the SSB query
        var selectedQuery = new Dictionary<string, string>();
        foreach (var (code, control) in _parameterControls)
        {
            if (control is ComboBox { SelectedItem: SsbValueOption selectedOption })
            {
                selectedQuery[code] = selectedOption.Code;
            }
        }

        if (selectedQuery.Count != _parameterControls.Count)
            return;

        // Show loading state
        if (ChartPlaceholder != null)
        {
            ChartPlaceholder.Text = "Fetching data from Statistics Norway (ssb.no)...";
            ChartPlaceholder.IsVisible = true;
        }
        if (ChartControl != null)
            ChartControl.IsVisible = false;
        if (ChartTitleTextBlock != null)
            ChartTitleTextBlock.IsVisible = false;

        if (FetchButton != null)
            FetchButton.IsEnabled = false;

        try
        {
            var dataResult = await _ssbDataService.FetchTableDataAsync(_currentMetadataItem.TableId, selectedQuery, _currentMetadataItem.Language);

            if (dataResult == null || dataResult.Values.Count == 0)
            {
                if (ChartPlaceholder != null)
                {
                    ChartPlaceholder.Text = "No data returned for the selected parameter combination.";
                    ChartPlaceholder.IsVisible = true;
                }
                return;
            }

            DisplayChartData(dataResult);
        }
        finally
        {
            UpdateFetchButtonState();
        }
    }

    private void DisplayChartData(SsbDataResult dataResult)
    {
        string seriesTitle = string.IsNullOrEmpty(dataResult.Unit)
            ? dataResult.Title
            : $"{dataResult.Title} ({dataResult.Unit})";

        var lineSeries = new LineSeries<double?>
        {
            Values = dataResult.Values,
            Name = seriesTitle,
            Stroke = new SolidColorPaint(new SKColor(33, 150, 243), 2.5f),
            GeometrySize = dataResult.Values.Count > 60 ? 0 : 6,
            GeometryStroke = new SolidColorPaint(new SKColor(33, 150, 243), 2f),
            GeometryFill = new SolidColorPaint(SKColors.White),
            Fill = new SolidColorPaint(new SKColor(33, 150, 243, 35))
        };

        if (ChartControl != null)
        {
            ChartControl.Series = new ISeries[] { lineSeries };
            ChartControl.XAxes = new Axis[]
            {
                new Axis
                {
                    Labels = dataResult.Labels,
                    LabelsRotation = dataResult.Labels.Count > 8 ? 45 : 0,
                    TextSize = 11
                }
            };
            ChartControl.YAxes = new Axis[]
            {
                new Axis
                {
                    Name = dataResult.Unit,
                    TextSize = 12
                }
            };

            ChartControl.IsVisible = true;
        }

        if (ChartTitleTextBlock != null)
        {
            ChartTitleTextBlock.Text = dataResult.Title;
            ChartTitleTextBlock.IsVisible = true;
        }

        if (ChartPlaceholder != null)
        {
            ChartPlaceholder.IsVisible = false;
        }
    }
}