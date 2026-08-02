using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using NoSilence.Settings;

namespace NoSilence.Tv;

/// <summary>
/// Wake-on-LAN and nothing else, for any display that supports it.
/// </summary>
/// <remarks>
/// Power state comes purely from whether the audio endpoint is present, which is free and
/// needs no protocol at all.
/// </remarks>
internal sealed class WakeOnLanDisplayController : IDisplayController
{
    private readonly TvSettings _settings;
    private readonly Func<bool> _endpointPresent;
    private readonly ILogger _log;

    public WakeOnLanDisplayController(TvSettings settings, Func<bool> endpointPresent, ILogger log)
    {
        _settings = settings;
        _endpointPresent = endpointPresent;
        _log = log;
    }

    public string Id => "wol";

    public string DisplayName => "Wake-on-LAN";

    public DisplayCapabilities Capabilities => DisplayCapabilities.Wake | DisplayCapabilities.PowerQuery;

    public event EventHandler<DisplayEvent>? Diagnostic;

    public Task<DisplayPowerState> GetPowerStateAsync(CancellationToken ct) =>
        Task.FromResult(_endpointPresent() ? DisplayPowerState.On : DisplayPowerState.Standby);

    public async Task<bool> WakeAsync(CancellationToken ct)
    {
        if (!WakeOnLan.TryParseMac(_settings.MacAddress, out byte[] mac))
        {
            Diagnostic?.Invoke(this, new DisplayEvent("No MAC address configured.", IsError: true));
            return false;
        }

        IPAddress? address = IPAddress.TryParse(_settings.Host, out IPAddress? parsed) ? parsed : null;
        int sent = await WakeOnLan.SendAsync(mac, address, _log, ct: ct).ConfigureAwait(false);

        Diagnostic?.Invoke(this, new DisplayEvent($"Sent {sent} Wake-on-LAN packet(s).", IsError: sent == 0));
        return sent > 0;
    }

    public Task<bool> SleepAsync(CancellationToken ct) => Task.FromResult(false);

    public Task<bool> SendVolumeAsync(VolumeCommand command, CancellationToken ct) => Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Runs whatever the user configured.
/// </summary>
/// <remarks>
/// The escape hatch that makes this useful beyond Samsung: an LG set via <c>webostv-cli</c>,
/// a Home Assistant webhook, a smart plug, or <c>cec-client</c> for anyone who does own a
/// USB CEC adapter. A command starting with <c>http</c> is fetched instead of executed.
/// </remarks>
internal sealed class ShellCommandDisplayController : IDisplayController
{
    private readonly TvSettings _settings;
    private readonly Func<bool> _endpointPresent;
    private readonly ILogger _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public ShellCommandDisplayController(TvSettings settings, Func<bool> endpointPresent, ILogger log)
    {
        _settings = settings;
        _endpointPresent = endpointPresent;
        _log = log;
    }

    public string Id => "shell";

    public string DisplayName => "Custom command";

    public DisplayCapabilities Capabilities
    {
        get
        {
            var caps = DisplayCapabilities.PowerQuery;
            if (!string.IsNullOrWhiteSpace(_settings.WakeCommand))
            {
                caps |= DisplayCapabilities.Wake;
            }

            if (!string.IsNullOrWhiteSpace(_settings.SleepCommand))
            {
                caps |= DisplayCapabilities.Sleep;
            }

            return caps;
        }
    }

    public event EventHandler<DisplayEvent>? Diagnostic;

    public async Task<DisplayPowerState> GetPowerStateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.StateCommand))
        {
            return _endpointPresent() ? DisplayPowerState.On : DisplayPowerState.Standby;
        }

        string? output = await RunAsync(_settings.StateCommand, ct).ConfigureAwait(false);

        return output?.Trim().ToLowerInvariant() switch
        {
            "on" => DisplayPowerState.On,
            "off" => DisplayPowerState.Off,
            "standby" => DisplayPowerState.Standby,
            null => DisplayPowerState.Unreachable,
            _ => DisplayPowerState.Unknown,
        };
    }

    public async Task<bool> WakeAsync(CancellationToken ct) =>
        await RunAsync(_settings.WakeCommand, ct).ConfigureAwait(false) is not null;

    public async Task<bool> SleepAsync(CancellationToken ct) =>
        await RunAsync(_settings.SleepCommand, ct).ConfigureAwait(false) is not null;

    public Task<bool> SendVolumeAsync(VolumeCommand command, CancellationToken ct) => Task.FromResult(false);

    private async Task<string?> RunAsync(string? command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        try
        {
            if (command.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                string body = await _http.GetStringAsync(command, ct).ConfigureAwait(false);
                Diagnostic?.Invoke(this, new DisplayEvent($"Fetched {command}"));
                return body;
            }

            var start = new ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            string output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            Diagnostic?.Invoke(this, new DisplayEvent($"Ran: {command} (exit {process.ExitCode})", IsError: process.ExitCode != 0));
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.ComponentModel.Win32Exception or IOException or TaskCanceledException or OperationCanceledException)
        {
            _log.LogWarning(ex, "Display command failed: {Command}", command);
            Diagnostic?.Invoke(this, new DisplayEvent($"Command failed: {ex.Message}", IsError: true));
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
