using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MemoryDebgugger.Services;
using System.Globalization;

namespace MemoryDebgugger;

public class MainWindow : Window
{
    private readonly SniMemoryStreamer _streamer = new(); // no fixed device URI anymore
    private readonly List<SymLabel> _allLabels = new();
    private readonly List<WatchedLabel> _watchedLabels = new();
    private readonly object _watchLock = new();

    private readonly TextBox _searchBox = new() { PlaceholderText = "search for label…" };
    private readonly ListBox _resultsList = new() { Height = 200 };
    private readonly StackPanel _watchPanel = new() { Spacing = 4 };
    private readonly TextBlock _statusText = new() { Text = "no .sym-file loaded." };

    private readonly ComboBox _deviceCombo = new() { PlaceholderText = "select device…", Width = 300 };
    private readonly Button _refreshDevicesButton = new() { Content = "scan devices" };
    private List<(string Uri, string DisplayName)> _devices = new();

    private List<SymLabel> _currentFiltered = new();
    private CancellationTokenSource? _watchLoopCts;

    public MainWindow()
    {
        Title = "SNES Memory Debugger";
        Width = 800;
        Height = 700;

        var loadButton = new Button { Content = "Load Sym-File" };
        loadButton.Click += LoadButton_Click;

        var subscribeButton = new Button { Content = "stream label" };
        subscribeButton.Click += SubscribeButton_Click;

        _searchBox.TextChanged += (_, _) => RefreshResultsList();
        _refreshDevicesButton.Click += RefreshDevicesButton_Click;
        _deviceCombo.SelectionChanged += (_, _) =>
        {
            if (_deviceCombo.SelectedIndex is >= 0 and var idx && idx < _devices.Count)
                _streamer.DeviceUri = _devices[idx].Uri;
        };

        var watchHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,1*,2*,Auto,Auto"),
            Margin = new Thickness(0, 8, 0, 4)
        };
        AddHeaderCell(watchHeader, "Label", 0);
        AddHeaderCell(watchHeader, "Value", 1);
        AddHeaderCell(watchHeader, "New Value", 2);

        var deviceRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { loadButton, _deviceCombo, _refreshDevicesButton }
        };
       

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    deviceRow,
                    _statusText,
                    _searchBox,
                    _resultsList,
                    subscribeButton,
                    new Separator(),
                    new TextBlock { Text = "streamed labels", FontWeight = FontWeight.Bold },
                    watchHeader,
                    _watchPanel
                }
            }
        };

        // scan for devices once at startup
        _ = RefreshDevicesAsync();
    }

    private static void AddHeaderCell(Grid grid, string text, int column)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeight.Bold };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private async void RefreshDevicesButton_Click(object? sender, RoutedEventArgs e)
        => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        try
        {
            _devices = (await _streamer.ListDevicesAsync()).ToList();
            _deviceCombo.ItemsSource = _devices.Select(d => d.DisplayName).ToList();

            if (_devices.Count > 0 && _deviceCombo.SelectedIndex < 0)
            {
                _deviceCombo.SelectedIndex = 0;
                _streamer.DeviceUri = _devices[0].Uri;
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error scanning devices: {ex.Message}";
        }
    }

    private async void LoadButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "select a WLA-DX .sym-file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("symbol-files") { Patterns = new[] { "*.sym" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        try
        {
            var parsed = SymFileParser.Parse(file.Path.LocalPath, false);
            _allLabels.Clear();
            _allLabels.AddRange(parsed);
            _statusText.Text = $"{_allLabels.Count} labels loaded from {file.Name}";
            RefreshResultsList();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error loading file: {ex.Message}";
        }
    }

    private void RefreshResultsList()
    {
        var filter = _searchBox.Text?.Trim() ?? "";

        _currentFiltered = filter.Length == 0
            ? _allLabels
            : _allLabels.Where(l => l.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        _resultsList.ItemsSource = _currentFiltered
            .Select(l => $"${l.AddressHex}  {l.Name}  ({l.Size} byte)")
            .ToList();
    }

    private void SubscribeButton_Click(object? sender, RoutedEventArgs e)
    {
        var index = _resultsList.SelectedIndex;
        if (index < 0 || index >= _currentFiltered.Count) return;

        var label = _currentFiltered[index];

        if (_watchedLabels.Any(w => w.Label.Name == label.Name && w.Label.Address == label.Address))
            return; // already streaming this label

        AddWatchRow(label);
        EnsureWatchLoopRunning();
    }

    private void AddWatchRow(SymLabel label)
    {
        var valueText = new TextBlock { Text = "—" };
        var writeBox = new TextBox { PlaceholderText = "e.g. $1F or 0x1F" };
        var sendButton = new Button { Content = "send" };
        var removeButton = new Button { Content = "✕" };

        var watched = new WatchedLabel
        {
            Label = label,
            ValueText = valueText,
            WriteValueBox = writeBox
        };

        sendButton.Click += async (_, _) => await SendValueAsync(watched);
        removeButton.Click += (_, _) => RemoveWatchRow(watched);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,1*,2*,Auto,Auto"),
            Tag = watched
        };

        var nameText = new TextBlock { Text = $"{label.Name} (${label.AddressHex})" };
        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(valueText, 1);
        Grid.SetColumn(writeBox, 2);
        Grid.SetColumn(sendButton, 3);
        Grid.SetColumn(removeButton, 4);

        row.Children.Add(nameText);
        row.Children.Add(valueText);
        row.Children.Add(writeBox);
        row.Children.Add(sendButton);
        row.Children.Add(removeButton);

        lock (_watchLock)
        {
            _watchedLabels.Add(watched);
        }

        _watchPanel.Children.Add(row);
    }

    private void RemoveWatchRow(WatchedLabel watched)
    {
        lock (_watchLock)
        {
            _watchedLabels.Remove(watched);
        }

        var row = _watchPanel.Children
            .OfType<Grid>()
            .FirstOrDefault(g => ReferenceEquals(g.Tag, watched));

        if (row is not null)
            _watchPanel.Children.Remove(row);
    }

    private void EnsureWatchLoopRunning()
    {
        if (_watchLoopCts is not null) return;

        _watchLoopCts = new CancellationTokenSource();

        _ = _streamer.RunWatchLoopAsync(
            getWatchedItems: () =>
            {
                lock (_watchLock)
                {
                    return _watchedLabels
                        .Select(w => (
                            Key: w.Label.Name,
                            FxPakProAddress: SnesAddress.ToFxPakPro(w.Label.Address),
                            Size: w.Label.Size))
                        .ToList();
                }
            },
            onValue: (key, data) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var watched = _watchedLabels.FirstOrDefault(w => w.Label.Name == key);
                    if (watched is null) return;

                    long value = -1;
                    switch (data.Length)
                    {
                        case 1:
                            value = data[0];
                            break;
                        case 2:
                            value = BitConverter.ToUInt16(data.ToArray(), 0);
                            break;
                        case 4:
                            value = BitConverter.ToInt32(data.ToArray(), 0);
                            break;
                        case 8:
                            value = BitConverter.ToInt64(data.ToArray(), 0);
                            break;
                    }

                    watched.ValueText.Text = $"hex: {Convert.ToHexString((data).Reverse().ToArray())}, dec: {value}";
                });
            },
            ct: _watchLoopCts.Token);
    }

    private async Task SendValueAsync(WatchedLabel watched)
    {
        var text = watched.WriteValueBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        else if (text.StartsWith('$'))
            text = text[1..];

        if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            watched.ValueText.Text = "invalid value";
            return;
        }

        var size = (int)watched.Label.Size;
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)(value >> (8 * i)); // little-endian
        }

        try
        {
            var fxAddress = SnesAddress.ToFxPakPro(watched.Label.Address);
            await _streamer.WriteAsync(fxAddress, bytes);
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => watched.ValueText.Text = $"Error: {ex.Message}");
        }
    }
}