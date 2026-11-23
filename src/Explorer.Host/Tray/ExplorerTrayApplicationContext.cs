using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Explorer.Host.Configuration;

namespace Explorer.Host.Tray;

public sealed class ExplorerTrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly Mutex _mutex;
    private readonly string[] _args;
    private WebApplication? _server;
    private bool _closing;

    public ExplorerTrayApplicationContext(string[] args, Mutex mutex)
    {
        _args = args;
        _mutex = mutex;
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Windows Datei-Explorer wird gestartet …",
            Visible = true,
            ContextMenuStrip = CreateMenu()
        };
        _trayIcon.DoubleClick += (_, _) => OpenWebApp();
        _ = StartServerAsync();
    }

    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Weboberfläche öffnen", null, (_, _) => OpenWebApp());
        menu.Items.Add("Tailscale-Adresse kopieren", null, (_, _) => CopyWebAddress());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Konfiguration öffnen", null, (_, _) => OpenPath(SettingsStore.SettingsPath));
        menu.Items.Add("Protokolle öffnen", null, (_, _) => OpenPath(Path.Combine(SettingsStore.DataDirectory, "logs")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => _ = ShutdownAsync());
        return menu;
    }

    private async Task StartServerAsync()
    {
        try
        {
            var settings = SettingsStore.Load();
            _server = ExplorerWebApp.Build(settings, _args);
            await _server.StartAsync();
            _trayIcon.Text = "Windows Datei-Explorer – Server läuft";
            _trayIcon.ShowBalloonTip(3500, "Datei-Explorer bereit", "Server läuft nur auf diesem PC und ist für Tailscale Serve vorbereitet.", ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            _trayIcon.Text = "Windows Datei-Explorer – Fehler";
            _trayIcon.ShowBalloonTip(6000, "Datei-Explorer konnte nicht starten", exception.Message, ToolTipIcon.Error);
        }
    }

    private void OpenWebApp()
    {
        var settings = SettingsStore.Load();
        var address = GetTailscaleAddress(settings);
        if (address is null)
        {
            MessageBox.Show("Tailscale ist nicht verfügbar. Starte Tailscale und führe setup.ps1 aus.",
                "Datei-Explorer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }

    private void CopyWebAddress()
    {
        var settings = SettingsStore.Load();
        var address = GetTailscaleAddress(settings);
        if (address is null)
        {
            MessageBox.Show("Tailscale konnte keine MagicDNS-Adresse liefern. Prüfe die Tailscale-Verbindung.",
                "Datei-Explorer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Clipboard.SetText(address);
        _trayIcon.ShowBalloonTip(2000, "Adresse kopiert", address, ToolTipIcon.Info);
    }

    private static string? GetTailscaleAddress(ExplorerSettings settings)
    {
        try
        {
            var start = new ProcessStartInfo("tailscale")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("status");
            start.ArgumentList.Add("--json");
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(3000)) return null;
            var json = process.StandardOutput.ReadToEnd();
            using var document = JsonDocument.Parse(json);
            var self = document.RootElement.GetProperty("Self");
            var dnsName = self.GetProperty("DNSName").GetString()?.TrimEnd('.');
            return string.IsNullOrWhiteSpace(dnsName) ? null : $"https://{dnsName}:{settings.TailscaleHttpsPort}/";
        }
        catch { return null; }
    }

    private static void OpenPath(string path)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) _ = SettingsStore.Load();
        }
        else Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async Task ShutdownAsync()
    {
        if (_closing) return;
        _closing = true;
        if (_server is not null)
        {
            try { await _server.StopAsync(TimeSpan.FromSeconds(5)); }
            catch { }
            await _server.DisposeAsync();
        }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _trayIcon.Dispose();
        base.Dispose(disposing);
    }
}
