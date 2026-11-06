using System.Collections.ObjectModel;
using System.Windows;
using Altrove.Capture;
using Altrove.Capture.Audio;
using Altrove.Capture.Input;
using Altrove.Capture.Microphone;
using Altrove.Capture.VirtualDisplay;
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

        // A virtual monitor left on the desktop by a crash: take it off before anything else.
        try
        {
            VirtualMonitor.Detach();
        }
        catch (InvalidOperationException)
        {
        }

        // The virtual monitor is Estendi's business, never a screen to duplicate.
        foreach (var monitor in Monitors.List().Where(m => !m.IsVirtual))
            MonitorChoices.Add(new Choice<MonitorInfo> { Label = monitor.DisplayName, Value = monitor });
        Select(MonitorChoices, MonitorChoices.FirstOrDefault(c => c.Value.DeviceName == _settings.LastMonitor) ?? MonitorChoices.FirstOrDefault());

        ModeChoices.Add(new Choice<string> { Label = "Duplica", Value = StreamModes.Mirror });
        ModeChoices.Add(new Choice<string> { Label = "Estendi", Value = StreamModes.Extend });
        Select(ModeChoices, ModeChoices.FirstOrDefault(c => c.Value == _settings.Mode) ?? ModeChoices[0]);
        foreach (var choice in ModeChoices)
            choice.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsExtend));

        foreach (var (label, value) in new[] { ("Automatica", 0), ("2160p", 2160), ("1440p", 1440), ("1080p", 1080), ("720p", 720) })
            ResolutionChoices.Add(new Choice<int> { Label = label, Value = value });
        SelectHeight(_settings.MaxHeight);

        foreach (var fps in new[] { 60, 30 })
            FpsChoices.Add(new Choice<int> { Label = $"{fps} fps", Value = fps });
        Select(FpsChoices, FpsChoices.FirstOrDefault(c => c.Value == _settings.Fps) ?? FpsChoices[0]);

        foreach (var mbps in new[] { 6, 12, 20, 30 })
            BitrateChoices.Add(new Choice<int> { Label = $"{mbps} Mbit/s", Value = mbps });
        Select(BitrateChoices, BitrateChoices.FirstOrDefault(c => c.Value == _settings.BitrateMbps) ?? BitrateChoices[1]);

        _softwareEncoder = _settings.SoftwareEncoder;
        _allowTouch = _settings.AllowTouch;
        _sendAudio = _settings.SendAudio;
        _useTabletMic = _settings.UseTabletMic;
        SelectedReceiver = Receivers.FirstOrDefault(r => r.Id == _settings.LastReceiverId);

        // While streaming, a choice made on the PC applies at once, like one from the receiver's panel.
        foreach (var choice in MonitorChoices.Cast<ObservableObject>().Concat(ModeChoices).Concat(ResolutionChoices).Concat(FpsChoices).Concat(BitrateChoices))
            choice.PropertyChanged += OnChoiceChanged;
        ResolutionChoices.CollectionChanged += (_, e) =>
        {
            foreach (var added in e.NewItems?.OfType<ObservableObject>() ?? [])
                added.PropertyChanged += OnChoiceChanged;
        };

        _browser.Changed += found => Application.Current?.Dispatcher.BeginInvoke(() => Merge(found));
        _browser.Start();
        _ = CheckForUpdatesInBackgroundAsync();
    }

    // ---- Choices changed on the PC during a stream ----

    /// <summary>The choices the running stream was started with.</summary>
    private (string? Mode, string? Monitor, int Height, int Fps, int Mbps, bool Software)? _applied;

    private (string? Mode, string? Monitor, int Height, int Fps, int Mbps, bool Software) CurrentChoices() =>
        (Selected(ModeChoices), Selected(MonitorChoices)?.DeviceName, Selected(ResolutionChoices), Selected(FpsChoices), Selected(BitrateChoices), SoftwareEncoder);

    /// <summary>Settings can be changed while idle and while streaming, not while connecting.</summary>
    public bool CanChangeChoices => State != SessionState.Connecting;

    private void OnChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "IsSelected")
            QueueLocalChange();
    }

    // After the current event: one change of chip unselects one and selects another.
    private void QueueLocalChange()
    {
        if (State == SessionState.Streaming)
            Application.Current?.Dispatcher.BeginInvoke(() => _ = ApplyLocalChangesAsync());
    }

    private async Task ApplyLocalChangesAsync()
    {
        await _reconfiguring.WaitAsync();
        try
        {
            if (_session is not { } session || State != SessionState.Streaming || _applied is not { } applied || CurrentChoices() == applied)
                return;
            if (IsExtend && !await EnsureVirtualDisplayDriverAsync())
            {
                RestoreChoices(applied);
                return;
            }
            SaveChoices();
            try
            {
                await StartStreamAsync(session, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException
                or SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
            {
                RestoreChoices(applied);
                SaveChoices();
                await StartStreamAsync(session, CancellationToken.None);
                ShowError($"Non sono riuscito a cambiare: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            App.Log($"Cambio impostazioni dal PC fallito: {ex}");
            await StopAsync(null);
            ShowError($"La trasmissione si è fermata cambiando le impostazioni: {ex.Message}");
        }
        finally
        {
            _reconfiguring.Release();
        }
    }

    private void RestoreChoices((string? Mode, string? Monitor, int Height, int Fps, int Mbps, bool Software) choices)
    {
        Select(ModeChoices, ModeChoices.FirstOrDefault(c => c.Value == choices.Mode));
        Select(MonitorChoices, MonitorChoices.FirstOrDefault(c => c.Value.DeviceName == choices.Monitor));
        SelectHeight(choices.Height);
        Select(FpsChoices, FpsChoices.FirstOrDefault(c => c.Value == choices.Fps));
        Select(BitrateChoices, BitrateChoices.FirstOrDefault(c => c.Value == choices.Mbps));
        SoftwareEncoder = choices.Software;
    }

    // ---- Updates (Velopack, from the GitHub releases) ----

    private readonly UpdateService _updates = new();
    private bool _restartAfterUpdate;

    /// <summary>The window closes so the updater can replace the app and start it again.</summary>
    public event EventHandler? UpdateRestartRequested;

    public string AppVersion => $"versione {UpdateService.CurrentVersion}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateReady), nameof(UpdateLabel))]
    private string? _updateReadyVersion;

    public bool IsUpdateReady => UpdateReadyVersion is not null;
    public string UpdateLabel => $"Aggiorna alla {UpdateReadyVersion}";

    /// <summary>Quietly downloads a newer release after start-up; offline or GitHub busy: nothing happens.</summary>
    private async Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            var version = await _updates.DownloadLatestAsync();
            Application.Current?.Dispatcher.BeginInvoke(() => UpdateReadyVersion = version);
        }
        catch (Exception ex)
        {
            App.Log($"Controllo aggiornamenti non riuscito: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RestartToUpdate()
    {
        if (!IsUpdateReady)
            return;
        _restartAfterUpdate = true;
        UpdateRestartRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>On exit, after the receiver got its goodbye: the updater takes over once this process is gone.</summary>
    private void ApplyDownloadedUpdate()
    {
        try
        {
            _updates.ApplyAfterExit(_restartAfterUpdate);
        }
        catch (Exception ex)
        {
            // Velopack applies a downloaded package at the next start anyway.
            App.Log($"Aggiornamento non avviato: {ex.Message}");
        }
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
    [NotifyPropertyChangedFor(nameof(StartStopLabel), nameof(IsIdle), nameof(CanChangeChoices))]
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

    /// <summary>Estendi chosen: the screen row does not apply, a new monitor of the receiver's size is made.</summary>
    public bool IsExtend => Selected(ModeChoices) == StreamModes.Extend;
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
        QueueLocalChange();
    }

    /// <summary>The receiver may work the PC by touch (taps become clicks on the streamed screen).</summary>
    [ObservableProperty]
    private bool _allowTouch;

    /// <summary>The PC's sound goes to the receiver too (if it can play it).</summary>
    [ObservableProperty]
    private bool _sendAudio;

    partial void OnSendAudioChanged(bool value)
    {
        _settings.SendAudio = value;
        _settings.Save();
        // While streaming, from the PC or from the receiver's panel: on or off at once.
        if (_session is { } session && State == SessionState.Streaming)
        {
            if (value)
                StartAudio(session);
            else
                StopAudio(session);
            session.SendSettings(CurrentSettings(null));
        }
    }

    private LoopbackAudio? _audio;

    // ---- The receiver's microphone as "Microfono Altrove" ----

    /// <summary>The tablet's microphone becomes a Windows microphone (needs usbip-win2, installed once).</summary>
    [ObservableProperty]
    private bool _useTabletMic;

    private MicBridge? _mic;

    partial void OnUseTabletMicChanged(bool value)
    {
        _settings.UseTabletMic = value;
        _settings.Save();
        if (value && !UsbIpSetup.IsInstalled)
        {
            _ = InstallMicAsync();
            return;
        }
        ApplyMic();
        if (_session is { } session && State == SessionState.Streaming)
            session.SendSettings(CurrentSettings(null));
    }

    /// <summary>The bridge runs while streaming with the box ticked and usbip-win2 installed.</summary>
    private void ApplyMic()
    {
        var wanted = UseTabletMic && UsbIpSetup.IsInstalled && _session is not null;
        if (wanted && _mic is null)
        {
            try
            {
                _mic = new MicBridge();
                App.Log("Microfono Altrove acceso.");
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                App.Log($"Microfono Altrove non avviato: {ex.Message}");
            }
        }
        else if (!wanted && _mic is not null)
        {
            App.Log($"Microfono Altrove spento: ricevuti {_mic.PacketsReceived} pacchetti di voce, {_mic.SecondsDecoded:0.0} s decodificati.");
            _mic.Dispose();
            _mic = null;
        }
    }

    /// <summary>The first time: explain, download the pinned usbip-win2 and install it (Windows asks for permission).</summary>
    private async Task InstallMicAsync()
    {
        var answer = MessageBox.Show(
            "Per usare il microfono del tablet nelle app del PC (Teams, Discord, Zoom…) Altrove crea un «Microfono Altrove». " +
            "Gli serve usbip-win2, un componente open source con driver firmati da Microsoft (circa 26 MB, da GitHub).\n\n" +
            "Windows chiederà il permesso di amministratore. Procedo?",
            "Altrove – microfono", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            UseTabletMic = false;
            return;
        }
        var previous = Status;
        try
        {
            Status = "Scarico usbip-win2…";
            var installer = await UsbIpSetup.DownloadInstallerAsync(CancellationToken.None);
            Status = "Installo usbip-win2…";
            var restart = await UsbIpSetup.InstallAsync(installer);
            Status = previous;
            if (restart)
                MessageBox.Show("usbip-win2 è installato. Riavvia Windows prima di usare il microfono.", "Altrove – microfono");
            ApplyMic();
            if (_session is { } session && State == SessionState.Streaming)
                session.SendSettings(CurrentSettings(null));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.IOException)
        {
            UseTabletMic = false;
            ShowError(ex is System.ComponentModel.Win32Exception
                ? "Installazione annullata: senza il permesso di Windows il microfono del tablet non è disponibile."
                : $"Il microfono del tablet non è disponibile: {ex.Message}");
        }
    }

    /// <summary>The format this receiver plays best, or null if it plays none (older receivers).</summary>
    private static string? AudioCodecFor(SenderSession? session) =>
        session is null ? null
        : session.ReceiverPlays(AudioCodecs.Opus) ? AudioCodecs.Opus
        : session.ReceiverPlays(AudioCodecs.Pcm) ? AudioCodecs.Pcm
        : null;

    private void StartAudio(SenderSession session)
    {
        if (_audio is not null || AudioCodecFor(session) is not { } codec)
            return;
        try
        {
            var audio = new LoopbackAudio(codec);
            session.StartAudio(audio.Format);
            audio.PacketReady += (packet, time) => session.SendAudio(packet, time);
            audio.Failed += ex => App.Log($"Audio interrotto: {ex.Message}");
            audio.Start();
            _audio = audio;
            App.Log($"Audio acceso: {codec}, da «{audio.DeviceName}».");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            App.Log($"Audio non disponibile: {ex.Message}");
        }
    }

    private void StopAudio(SenderSession session)
    {
        App.Log($"Audio spento (era {(_audio is null ? "già spento" : "acceso")}).");
        _audio?.Dispose();
        _audio = null;
        session.StopAudio();
    }

    partial void OnAllowTouchChanged(bool value)
    {
        _settings.AllowTouch = value;
        _settings.Save();
        if (!value && _streamTarget is { } target)
        {
            // Fingers still down, or a button held by a trackpad drag, would stay pressed.
            _touch?.SetTarget(target);
            _mouse?.ReleaseButtons();
        }
        if (_session is { } session && State == SessionState.Streaming)
            session.SendSettings(CurrentSettings(null));
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
        SaveChoices();
        if (IsExtend && !await EnsureVirtualDisplayDriverAsync())
            return;

        State = SessionState.Connecting;
        HasError = false;
        Stats = "";
        Status = $"Collegamento a {receiver.Name}…";
        _connecting = new CancellationTokenSource();
        try
        {
            List<string> features = [Features.Configure, Features.Scale, Features.Audio, Features.Mic];
            // Always offered: the PC's check box can turn them on and off during the stream.
            features.AddRange([Features.Touch, Features.Mouse]);
            var self = new HelloMessage(AltroveProtocol.Version, Roles.Sender, _settings.DeviceId, Environment.MachineName,
                typeof(MainViewModel).Assembly.GetName().Version?.ToString(3), Features: features);
            var session = await SenderSession.ConnectAsync(ReceiverAddress.ToUri(receiver.Host, receiver.Port), self, _connecting.Token);
            _session = session;
            _receiverName = receiver.Name = session.Receiver.Name is { Length: > 0 } name ? name : receiver.Name;
            session.StatsUpdated += OnStats;
            session.Closed += why => Application.Current?.Dispatcher.BeginInvoke(() => _ = StopAsync(why));
            session.ConfigureRequested += request => Application.Current?.Dispatcher.BeginInvoke(() => _ = ConfigureAsync(request));
            {
                try
                {
                    _touch = new TouchInjector(monitor);
                    session.TouchReceived += OnTouch;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    _touch = null; // Windows too old for synthetic touch: stream without it
                }
                _mouse = new MouseInjector(monitor);
                session.MouseReceived += OnMouse;
            }

            await StartStreamAsync(session, _connecting.Token);
            // The receiver's microphone goes to "Microfono Altrove" whenever a bridge is running.
            session.MicStarted += format => _mic?.Begin(format);
            session.MicStopped += () => _mic?.End();
            session.MicPacketReceived += packet => _mic?.Push(packet);
            if (SendAudio)
                StartAudio(session);
            ApplyMic();
            session.SendSettings(CurrentSettings(null)); // the first settings went out before sound and microphone were on
            State = SessionState.Streaming;
        }
        catch (OperationCanceledException) when (_connecting?.IsCancellationRequested == true)
        {
            await StopAsync("Collegamento annullato.");
        }
        catch (OperationCanceledException)
        {
            // The connection's own timeout, not the user.
            await StopAsync(null);
            ShowError($"{receiver.Name} non ha risposto in tempo. Controlla che l'app Altrove sia aperta sul ricevitore.");
        }
        catch (Exception ex)
        {
            await StopAsync(null);
            ShowError(Describe(ex, receiver));
        }
    }

    private string _receiverName = "";
    private TouchInjector? _touch;
    private readonly SemaphoreSlim _reconfiguring = new(1, 1);

    private MouseInjector? _mouse;
    private MonitorInfo? _streamTarget;

    private void OnTouch(TouchMessage touch)
    {
        if (AllowTouch)
            _touch?.Inject(touch);
    }

    private void OnMouse(MouseMessage mouse)
    {
        if (AllowTouch)
            _mouse?.Inject(mouse);
    }

    /// <summary>Selects the resolution chip for <paramref name="height"/>, adding one if a receiver picked a height the PC does not list.</summary>
    private void SelectHeight(int height)
    {
        var chip = ResolutionChoices.FirstOrDefault(c => c.Value == height);
        if (chip is null)
        {
            chip = new Choice<int> { Label = $"{height}p", Value = height };
            var index = ResolutionChoices.Skip(1).TakeWhile(c => c.Value > height).Count() + 1;
            ResolutionChoices.Insert(index, chip);
        }
        Select(ResolutionChoices, chip);
    }

    // The receiver's screen and decoder limits, in landscape, for the resolution choices.
    private static (int Width, int Height, int MaxWidth, int MaxHeight) ReceiverLimits(SenderSession session)
    {
        var display = session.Receiver.Display;
        var width = display?.Width ?? 1920;
        var height = display?.Height ?? 1080;
        return (Math.Max(width, height), Math.Min(width, height),
            session.Receiver.MaxWidth ?? Math.Max(width, height), session.Receiver.MaxHeight ?? Math.Min(width, height));
    }

    private IReadOnlyList<ResolutionChoice> ResolutionsFor(SenderSession session)
    {
        var (w, h, maxW, maxH) = ReceiverLimits(session);
        if (IsExtend)
            return Altrove.Core.Video.ResolutionPlanner.Extend(w, h, maxW, maxH);
        var screen = Selected(MonitorChoices)!;
        return Altrove.Core.Video.ResolutionPlanner.Mirror(screen.Width, screen.Height, maxW, maxH, w, h);
    }

    /// <summary>
    /// Starts streaming with the choices as they are now, or switches the running stream to them: the old
    /// source goes first, then the virtual monitor is attached, resized or detached, then the new source starts.
    /// </summary>
    private async Task StartStreamAsync(SenderSession session, CancellationToken token)
    {
        var extend = IsExtend;
        var maxHeight = Selected(ResolutionChoices);
        var fps = Selected(FpsChoices);
        var previous = _source;
        _source = null;
        if (previous is not null)
        {
            session.DetachSource();
            await previous.DisposeAsync();
        }

        MonitorInfo target;
        int width, height;
        if (extend)
        {
            // A new monitor exactly the receiver's shape, streamed 1:1: no scaling, no black bars.
            Status = $"Creo lo schermo virtuale per {_receiverName}…";
            var (w, h, maxW, maxH) = ReceiverLimits(session);
            var size = Altrove.Core.Video.ResolutionPlanner.ExtendSize(w, h, maxW, maxH, maxHeight);
            _virtualAttached = true;
            target = await VirtualMonitor.AttachAsync(new DisplayMode(size.Width, size.Height, fps), token);
            (width, height) = (target.Width & ~1, target.Height & ~1);
            if (_settings.ExtendScale > 0 && !DisplayScale.Set(target.DeviceName, _settings.ExtendScale))
                App.Log($"Scala {_settings.ExtendScale}% non applicata a {target.DeviceName}.");
        }
        else
        {
            if (_virtualAttached)
            {
                _virtualAttached = false;
                VirtualMonitor.Detach();
            }
            // Monitor handles do not survive a change of the desktop layout: look the screen up again.
            var chosen = Selected(MonitorChoices)!;
            target = Monitors.List().FirstOrDefault(m => m.DeviceName.Equals(chosen.DeviceName, StringComparison.OrdinalIgnoreCase)) ?? chosen;
            var (w, h, maxW, maxH) = ReceiverLimits(session);
            (width, height) = Altrove.Core.Video.ResolutionPlanner.MirrorSize(target.Width, target.Height, maxW, maxH, w, h, maxHeight);
        }

        var settings = new EncoderSettings(width, height, fps, Selected(BitrateChoices) * 1000);
        var source = new ScreenVideoSource(target, SoftwareEncoder);
        _source = source;
        var streamMode = extend ? StreamModes.Extend : StreamModes.Mirror;
        if (session.IsStarted)
            await session.SwitchSourceAsync(source, settings, streamMode, token);
        else
            await session.StartAsync(source, settings, streamMode, token);
        _touch?.SetTarget(target);
        _mouse?.SetTarget(target);
        _streamTarget = target;
        _applied = CurrentChoices();
        session.SendSettings(CurrentSettings(null));
        Status = extend
            ? $"{_receiverName} è un nuovo schermo a destra degli altri: {width}×{height}, {fps} fps · {source.EncoderName}"
            : $"In trasmissione su {_receiverName}: {width}×{height}, {fps} fps · {source.EncoderName}";
    }

    /// <summary>What is streaming and what the receiver may change, for its settings panel.</summary>
    private SettingsMessage CurrentSettings(string? notice)
    {
        List<string> modes = [StreamModes.Mirror];
        if (VirtualMonitor.IsDriverInstalled && !VirtualMonitor.IsRemoteSession)
            modes.Add(StreamModes.Extend);

        // The resolutions as this receiver would get them now; the one in use, never "automatic".
        IReadOnlyList<ResolutionChoice>? resolutions = null;
        var maxHeight = Selected(ResolutionChoices);
        if (_session is { } session)
        {
            resolutions = ResolutionsFor(session);
            var streaming = _streamTarget is { } t && IsExtend ? (t.Width & ~1, t.Height & ~1) : ((int, int)?)null;
            var inUse = resolutions.FirstOrDefault(r => r.MaxHeight == maxHeight)
                ?? (streaming is { } s ? resolutions.FirstOrDefault(r => (r.Width, r.Height) == s) : null)
                ?? resolutions.FirstOrDefault(r => r.Recommended);
            if (inUse is not null)
                maxHeight = inUse.MaxHeight;
        }

        // The virtual monitor's Windows scale, only in Estendi.
        int? scale = null;
        IReadOnlyList<int> scales = [];
        if (IsExtend && _streamTarget is { IsVirtual: true } monitor && DisplayScale.Get(monitor.DeviceName) is { } current)
        {
            scale = current.Current;
            scales = current.Available;
        }

        return new SettingsMessage(
            Selected(ModeChoices) ?? StreamModes.Mirror,
            Selected(MonitorChoices)?.DeviceName,
            maxHeight,
            Selected(FpsChoices),
            Selected(BitrateChoices) * 1000,
            new SettingsChoices(
                modes,
                MonitorChoices.Select(c => new ScreenChoice(c.Value.DeviceName, c.Label)).ToList(),
                ResolutionChoices.Select(c => c.Value).ToList(),
                FpsChoices.Select(c => c.Value).ToList(),
                BitrateChoices.Select(c => c.Value * 1000).ToList(),
                resolutions,
                scales),
            notice,
            scale,
            AudioCodecFor(_session) is null ? null : _audio is not null,
            AllowTouch,
            UsbIpSetup.IsInstalled ? _mic is not null : null);
    }

    /// <summary>The receiver asked for other settings: apply them to the running stream, or put the old ones back.</summary>
    private async Task ConfigureAsync(ConfigureMessage request)
    {
        await _reconfiguring.WaitAsync();
        try
        {
            if (_session is not { } session || State != SessionState.Streaming)
                return;
            var before = (Mode: Selected(ModeChoices), Monitor: Selected(MonitorChoices), Height: Selected(ResolutionChoices),
                Fps: Selected(FpsChoices), Mbps: Selected(BitrateChoices));

            string? notice = null;
            if (request.Mode is { } mode && mode != before.Mode)
            {
                if (mode == StreamModes.Extend && !CurrentSettings(null).Choices.Modes.Contains(StreamModes.Extend))
                    notice = "Sul PC lo schermo virtuale non è disponibile: attivalo una volta scegliendo Estendi in Altrove sul PC.";
                else
                {
                    Select(ModeChoices, ModeChoices.FirstOrDefault(c => c.Value == mode));
                    // The other mode has other resolutions: start from the recommended one.
                    if (request.MaxHeight is null)
                        SelectHeight(0);
                }
            }
            if (request.Screen is { } screen)
                Select(MonitorChoices, MonitorChoices.FirstOrDefault(c => c.Value.DeviceName == screen) ?? MonitorChoices.FirstOrDefault(c => c.IsSelected));
            if (request.MaxHeight is { } height and >= 0)
                SelectHeight(height);

            // The scale changes the virtual monitor in place: no new stream.
            if (request.Scale is { } percent && IsExtend && _streamTarget is { IsVirtual: true } virtualMonitor)
            {
                if (DisplayScale.Set(virtualMonitor.DeviceName, percent))
                {
                    _settings.ExtendScale = percent;
                    _settings.Save();
                }
                else
                    notice = $"Windows non ha accettato la scala {percent}%.";
            }
            if (request.Fps is { } fps)
                Select(FpsChoices, FpsChoices.FirstOrDefault(c => c.Value == fps) ?? FpsChoices.First(c => c.IsSelected));
            if (request.BitrateKbps is { } kbps)
                Select(BitrateChoices, BitrateChoices.FirstOrDefault(c => c.Value * 1000 == kbps) ?? BitrateChoices.First(c => c.IsSelected));

            var after = (Mode: Selected(ModeChoices), Monitor: Selected(MonitorChoices), Height: Selected(ResolutionChoices),
                Fps: Selected(FpsChoices), Mbps: Selected(BitrateChoices));
            // Sound on or off changes nothing else.
            if (request.Audio is { } wantAudio)
                SendAudio = wantAudio; // OnSendAudioChanged turns it on or off
            if (request.Mic is { } wantMic && (UsbIpSetup.IsInstalled || !wantMic))
                UseTabletMic = wantMic; // OnUseTabletMicChanged too

            if (after == before)
            {
                session.SendSettings(CurrentSettings(notice));
                return;
            }
            SaveChoices();
            try
            {
                await StartStreamAsync(session, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException
                or SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
            {
                // Back to what worked, and tell the receiver why.
                Select(ModeChoices, ModeChoices.FirstOrDefault(c => c.Value == before.Mode));
                Select(MonitorChoices, MonitorChoices.FirstOrDefault(c => c.Value == before.Monitor));
                SelectHeight(before.Height);
                Select(FpsChoices, FpsChoices.FirstOrDefault(c => c.Value == before.Fps));
                Select(BitrateChoices, BitrateChoices.FirstOrDefault(c => c.Value == before.Mbps));
                SaveChoices();
                await StartStreamAsync(session, CancellationToken.None);
                session.SendSettings(CurrentSettings($"Non sono riuscito a cambiare: {ex.Message}"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"Cambio impostazioni fallito: {ex}");
            await StopAsync(null);
            ShowError($"La trasmissione si è fermata cambiando le impostazioni: {ex.Message}");
        }
        finally
        {
            _reconfiguring.Release();
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
            session.TouchReceived -= OnTouch;
            session.MouseReceived -= OnMouse;
            await session.DisposeAsync();
        }
        _touch?.Dispose();
        _touch = null;
        _mouse = null;
        if (_mic is not null)
            App.Log($"Microfono Altrove spento: ricevuti {_mic.PacketsReceived} pacchetti di voce, {_mic.SecondsDecoded:0.0} s decodificati.");
        _mic?.Dispose();
        _mic = null;
        _applied = null;
        _audio?.Dispose();
        _audio = null;
        _streamTarget = null;
        if (source is not null)
            await source.DisposeAsync();
        if (_virtualAttached)
        {
            _virtualAttached = false;
            try
            {
                VirtualMonitor.Detach();
            }
            catch (InvalidOperationException ex)
            {
                why = $"Non sono riuscito a staccare lo schermo virtuale: {ex.Message}";
            }
        }
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

    private void SaveChoices()
    {
        if (SelectedReceiver is { } receiver)
            _settings.LastReceiverId = receiver.Id;
        if (Selected(MonitorChoices) is { } monitor)
            _settings.LastMonitor = monitor.DeviceName;
        _settings.MaxHeight = Selected(ResolutionChoices);
        _settings.Fps = Selected(FpsChoices);
        _settings.BitrateMbps = Selected(BitrateChoices);
        _settings.Mode = Selected(ModeChoices) ?? StreamModes.Mirror;
        _settings.Save();
    }

    private bool _virtualAttached;

    /// <summary>
    /// The first time Estendi is used: explain, then install the driver through an elevated copy of
    /// Altrove (Windows asks for permission). False if the user declines or it fails.
    /// </summary>
    private async Task<bool> EnsureVirtualDisplayDriverAsync()
    {
        if (VirtualMonitor.IsDriverInstalled)
            return true;
        var answer = MessageBox.Show(
            "Per estendere lo schermo Altrove installa un driver video, Virtual Display Driver (open source e firmato), " +
            "che aggiunge a Windows un monitor virtuale. Il monitor esiste solo mentre trasmetti.\n\n" +
            "Windows chiederà il permesso di amministratore. Procedo?",
            "Altrove – schermo virtuale", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return false;

        Status = "Installo lo schermo virtuale…";
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, App.InstallVirtualDisplayArgument)
            {
                Verb = "runas",
                UseShellExecute = true,
            })!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !VirtualMonitor.IsDriverInstalled)
            {
                ShowError("L'installazione dello schermo virtuale non è riuscita.");
                return false;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowError("Installazione annullata: senza il permesso di Windows Estendi non è disponibile.");
            return false;
        }
        // The new driver puts its monitor on the desktop at once: off it goes until a stream needs it.
        await Task.Delay(1500);
        try
        {
            VirtualMonitor.Detach();
        }
        catch (InvalidOperationException)
        {
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _browser.Dispose();
        await StopAsync(null);
        ApplyDownloadedUpdate();
    }
}

public enum SessionState
{
    Idle,
    Connecting,
    Streaming,
}
