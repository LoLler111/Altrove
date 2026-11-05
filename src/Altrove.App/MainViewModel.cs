using System.Collections.ObjectModel;
using System.Windows;
using Altrove.Capture;
using Altrove.Core.Discovery;
using Altrove.Core.Protocol;
using Altrove.Core.Streaming;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Altrove.App;

public sealed partial class ReceiverItem : ObservableObject
{
    public required string Id { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public bool IsManual { get; init; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private bool _isOnline;

    public string Address => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
    public string Detail => IsManual ? $"{Address} · aggiunto a mano" : $"{Address} · trovato in rete";
}

/// <summary>One pill in a row of choices (screen, resolution, frame rate, bitrate).</summary>
public sealed partial class Choice<T> : ObservableObject
{
    public required string Label { get; init; }
    public required T Value { get; init; }
    public bool IsAvailable { get; init; } = true;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly MdnsBrowser _browser = new();
    private SenderSession? _session;
    private ScreenVideoSource? _source;

    public MainViewModel()
    {
        foreach (var address in _settings.ManualAddresses)
            AddManual(address, save: false);

        foreach (var monitor in Monitors.List())
            MonitorChoices.Add(new Choice<MonitorInfo> { Label = monitor.DisplayName, Value = monitor });
        Select(MonitorChoices, MonitorChoices.FirstOrDefault(c => c.Value.DeviceName == _settings.LastMonitor) ?? MonitorChoices.FirstOrDefault());

        ModeChoices.Add(new Choice<string> { Label = "Duplica", Value = StreamModes.Mirror });
        ModeChoices.Add(new Choice<string> { Label = "Estendi (in arrivo)", Value = StreamModes.Extend, IsAvailable = false });
        Select(ModeChoices, ModeChoices[0]);

        foreach (var (label, value) in new[] { ("Automatica", 0), ("1080p", 1080), ("720p", 720) })
            ResolutionChoices.Add(new Choice<int> { Label = label, Value = value });
        Select(ResolutionChoices, ResolutionChoices.FirstOrDefault(c => c.Value == _settings.MaxHeight) ?? ResolutionChoices[0]);

        foreach (var fps in new[] { 60, 30 })
            FpsChoices.Add(new Choice<int> { Label = $"{fps} fps", Value = fps });
        Select(FpsChoices, FpsChoices.FirstOrDefault(c => c.Value == _settings.Fps) ?? FpsChoices[0]);

        foreach (var mbps in new[] { 6, 12, 20, 30 })
            BitrateChoices.Add(new Choice<int> { Label = $"{mbps} Mbit/s", Value = mbps });
        Select(BitrateChoices, BitrateChoices.FirstOrDefault(c => c.Value == _settings.BitrateMbps) ?? BitrateChoices[1]);

        _softwareEncoder = _settings.SoftwareEncoder;
        SelectedReceiver = Receivers.FirstOrDefault(r => r.Id == _settings.LastReceiverId);

        _browser.Changed += found => Application.Current?.Dispatcher.BeginInvoke(() => Merge(found));
        _browser.Start();
    }

    public ObservableCollection<ReceiverItem> Receivers { get; } = [];
    public ObservableCollection<Choice<MonitorInfo>> MonitorChoices { get; } = [];
    public ObservableCollection<Choice<string>> ModeChoices { get; } = [];
    public ObservableCollection<Choice<int>> ResolutionChoices { get; } = [];
    public ObservableCollection<Choice<int>> FpsChoices { get; } = [];
    public ObservableCollection<Choice<int>> BitrateChoices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    [NotifyPropertyChangedFor(nameof(CanRemoveSelected))]
    private ReceiverItem? _selectedReceiver;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddAddressCommand))]
    private string _manualAddress = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopLabel), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    private SessionState _state = SessionState.Idle;

    [ObservableProperty]
    private string _status = "Scegli un ricevitore e premi Trasmetti.";

    [ObservableProperty]
    private string _stats = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _softwareEncoder;

    public bool IsIdle => State == SessionState.Idle;
    public bool CanRemoveSelected => SelectedReceiver?.IsManual == true;
    public string StartStopLabel => State switch
    {
        SessionState.Idle => "Trasmetti",
        SessionState.Connecting => "Annulla",
        _ => "Ferma",
    };

    public bool HasReceivers => Receivers.Count > 0;

    partial void OnSoftwareEncoderChanged(bool value)
    {
        _settings.SoftwareEncoder = value;
        _settings.Save();
    }

    private static void Select<T>(IEnumerable<Choice<T>> choices, Choice<T>? chosen)
    {
        foreach (var choice in choices)
            choice.IsSelected = ReferenceEquals(choice, chosen);
    }

    private static T? Selected<T>(IEnumerable<Choice<T>> choices) => choices.FirstOrDefault(c => c.IsSelected) is { } c ? c.Value : default;

    private void Merge(IReadOnlyList<DiscoveredReceiver> found)
    {
        var ids = found.Select(f => f.Id).ToHashSet();
        foreach (var gone in Receivers.Where(r => !r.IsManual && !ids.Contains(r.Id) && r != SelectedReceiver).ToList())
            Receivers.Remove(gone);
        foreach (var item in Receivers.Where(r => !r.IsManual))
            item.IsOnline = ids.Contains(item.Id);
        foreach (var receiver in found)
        {
            var existing = Receivers.FirstOrDefault(r => !r.IsManual && r.Id == receiver.Id);
            if (existing is not null && existing.Host == receiver.Host && existing.Port == receiver.Port)
            {
                existing.Name = receiver.Name;
                continue;
            }
            var index = existing is null ? Receivers.Count(r => !r.IsManual) : Receivers.IndexOf(existing);
            var item = new ReceiverItem { Id = receiver.Id, Host = receiver.Host, Port = receiver.Port, Name = receiver.Name, IsOnline = true };
            if (existing is not null)
            {
                var wasSelected = existing == SelectedReceiver;
                Receivers[index] = item;
                if (wasSelected)
                    SelectedReceiver = item;
            }
            else
                Receivers.Insert(index, item);
            if (SelectedReceiver is null && receiver.Id == _settings.LastReceiverId)
                SelectedReceiver = item;
        }
        OnPropertyChanged(nameof(HasReceivers));
    }

    private bool CanAddAddress() => ReceiverAddress.TryParse(ManualAddress, out _, out _);

    [RelayCommand(CanExecute = nameof(CanAddAddress))]
    private void AddAddress()
    {
        if (AddManual(ManualAddress, save: true) is { } item)
        {
            SelectedReceiver = item;
            ManualAddress = "";
        }
    }

    private ReceiverItem? AddManual(string text, bool save)
    {
        if (!ReceiverAddress.TryParse(text, out var host, out var port))
            return null;
        var id = $"manual:{host}:{port}";
        var existing = Receivers.FirstOrDefault(r => r.Id == id);
        if (existing is not null)
            return existing;
        var item = new ReceiverItem { Id = id, Host = host, Port = port, IsManual = true, Name = host };
        Receivers.Add(item);
        if (save)
        {
            _settings.ManualAddresses.Add(item.Address);
            _settings.Save();
        }
        OnPropertyChanged(nameof(HasReceivers));
        return item;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedReceiver is not { IsManual: true } item)
            return;
        Receivers.Remove(item);
        _settings.ManualAddresses.RemoveAll(a => a == item.Address);
        _settings.Save();
        SelectedReceiver = null;
        OnPropertyChanged(nameof(HasReceivers));
    }

    private bool CanStartStop() => State != SessionState.Idle || SelectedReceiver is not null;

    private CancellationTokenSource? _connecting;

    [RelayCommand(CanExecute = nameof(CanStartStop))]
    private async Task StartStop()
    {
        switch (State)
        {
            case SessionState.Connecting:
                _connecting?.Cancel();
                return;
            case SessionState.Streaming:
                await StopAsync("Trasmissione fermata.");
                return;
        }

        var receiver = SelectedReceiver!;
        var monitor = Selected(MonitorChoices);
        if (monitor is null)
        {
            ShowError("Nessuno schermo da trasmettere.");
            return;
        }
        SaveChoices(receiver, monitor);

        State = SessionState.Connecting;
        HasError = false;
        Stats = "";
        Status = $"Collegamento a {receiver.Name}…";
        _connecting = new CancellationTokenSource();
        try
        {
            var self = new HelloMessage(AltroveProtocol.Version, Roles.Sender, _settings.DeviceId, Environment.MachineName,
                typeof(MainViewModel).Assembly.GetName().Version?.ToString(3));
            var session = await SenderSession.ConnectAsync(ReceiverAddress.ToUri(receiver.Host, receiver.Port), self, _connecting.Token);
            _session = session;
            if (session.Receiver.Name is { Length: > 0 } name)
                receiver.Name = name;

            var maxHeight = Selected(ResolutionChoices);
            var (width, height) = session.PlanSize(monitor.Width, monitor.Height, maxHeight > 0 ? maxHeight : null);
            var settings = new EncoderSettings(width, height, Selected(FpsChoices), Selected(BitrateChoices) * 1000);
            session.StatsUpdated += OnStats;
            session.Closed += why => Application.Current?.Dispatcher.BeginInvoke(() => _ = StopAsync(why));

            _source = new ScreenVideoSource(monitor, SoftwareEncoder);
            await session.StartAsync(_source, settings, StreamModes.Mirror, _connecting.Token);
            State = SessionState.Streaming;
            Status = $"In trasmissione su {receiver.Name}: {width}×{height}, {settings.Fps} fps · {_source.EncoderName}";
        }
        catch (OperationCanceledException)
        {
            await StopAsync("Collegamento annullato.");
        }
        catch (Exception ex)
        {
            await StopAsync(null);
            ShowError(Describe(ex, receiver));
        }
    }

    private static string Describe(Exception ex, ReceiverItem receiver) => ex switch
    {
        System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException or System.Net.Http.HttpRequestException =>
            $"{receiver.Name} non risponde su {receiver.Address}. Controlla che l'app Altrove sia aperta sul ricevitore e che siate sulla stessa rete.",
        TimeoutException => $"{receiver.Name} non ha risposto in tempo.",
        _ => ex.Message,
    };

    private void ShowError(string message)
    {
        HasError = true;
        Status = message;
    }

    private void OnStats(SenderStats s) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        var parts = new List<string> { $"{s.SentFps:0} fps", $"{s.Mbps:0.0} Mbit/s" };
        if (s.RoundTripMs is { } rtt)
            parts.Add($"rete {rtt:0} ms");
        if (s.ReceiverFps is { } fps)
            parts.Add($"ricevitore {fps:0} fps");
        if (s.DroppedFrames > 0)
            parts.Add($"{s.DroppedFrames} scartati");
        Stats = string.Join(" · ", parts);
    });

    private async Task StopAsync(string? why)
    {
        var session = _session;
        var source = _source;
        _session = null;
        _source = null;
        if (session is not null)
        {
            session.StatsUpdated -= OnStats;
            await session.DisposeAsync();
        }
        if (source is not null)
            await source.DisposeAsync();
        _connecting?.Dispose();
        _connecting = null;
        State = SessionState.Idle;
        Stats = "";
        if (why is not null)
        {
            HasError = false;
            Status = why;
        }
    }

    private void SaveChoices(ReceiverItem receiver, MonitorInfo monitor)
    {
        _settings.LastReceiverId = receiver.Id;
        _settings.LastMonitor = monitor.DeviceName;
        _settings.MaxHeight = Selected(ResolutionChoices);
        _settings.Fps = Selected(FpsChoices);
        _settings.BitrateMbps = Selected(BitrateChoices);
        _settings.Save();
    }

    public async ValueTask DisposeAsync()
    {
        _browser.Dispose();
        await StopAsync(null);
    }
}

public enum SessionState
{
    Idle,
    Connecting,
    Streaming,
}
