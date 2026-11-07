using System.IO;
using System.Text.Json;

namespace Altrove.App;

/// <summary>What the app remembers between runs, in %APPDATA%\Altrove\settings.json.</summary>
public sealed class AppSettings
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public List<string> ManualAddresses { get; set; } = [];
    public string? LastReceiverId { get; set; }
    public string? LastMonitor { get; set; }
    public int MaxHeight { get; set; }
    public int Fps { get; set; } = 60;
    public int BitrateMbps { get; set; } = 12;
    public bool SoftwareEncoder { get; set; }
    public string Mode { get; set; } = "mirror";
    public bool AllowTouch { get; set; } = true;
    /// <summary>Windows scale of the virtual monitor in percent, 0 = what Windows picks.</summary>
    public int ExtendScale { get; set; }
    public bool SendAudio { get; set; } = true;
    public bool UseTabletMic { get; set; }
    /// <summary>The bitrate follows the network, up to the chosen one.</summary>
    public bool AdaptiveBitrate { get; set; } = true;
    /// <summary>Receiver id → the token it gave when paired with its PIN.</summary>
    public Dictionary<string, string> PairingTokens { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Altrove", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
