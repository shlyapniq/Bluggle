using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Bluggle.Bluetooth;
using Bluggle.Services;

namespace Bluggle.Views;

/// <summary>
/// Plain read-into-controls / write-back-on-save form. Deliberately non-modal and non-topmost-
/// stealing: the widget keeps polling and toggling while this is open.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly WidgetController _controller;

    public SettingsWindow(WidgetController controller)
    {
        _controller = controller;
        InitializeComponent();
        LoadFromConfig();
    }

    private AppConfig Config => _controller.Config;

    private void LoadFromConfig()
    {
        PopulateDevices();

        ShowAllCheck.IsChecked = Config.ShowAllPairedDevices;
        EnableMissingProfilesCheck.IsChecked = Config.EnableMissingProfilesOnConnect;
        PlaySoundCheck.IsChecked = Config.PlaySoundOnConnect;
        NoActivateCheck.IsChecked = Config.NoActivate;
        StartupCheck.IsChecked = StartupManager.IsEnabled();

        PollBox.Text = Config.PollIntervalMs.ToString(CultureInfo.InvariantCulture);
        ConnectTimeoutBox.Text = Config.ConnectTimeoutMs.ToString(CultureInfo.InvariantCulture);
        DisconnectTimeoutBox.Text = Config.DisconnectTimeoutMs.ToString(CultureInfo.InvariantCulture);
        RetryIntervalBox.Text = Config.LinkRetryIntervalMs.ToString(CultureInfo.InvariantCulture);
        SoundDelayBox.Text = Config.SoundDelayMs.ToString(CultureInfo.InvariantCulture);

        SizeBox.Text = Config.WidgetSize.ToString("0.##", CultureInfo.InvariantCulture);
        OpacityBox.Text = Config.IdleOpacity.ToString("0.##", CultureInfo.InvariantCulture);
        AccentBox.Text = Config.AccentColor;

        ServicesBox.Text = string.Join(Environment.NewLine, Config.ServiceGuids);
    }

    private void PopulateDevices()
    {
        bool showAll = ShowAllCheck.IsChecked ?? Config.ShowAllPairedDevices;

        List<PairedDevice> devices = _controller.Devices
            .Where(d => showAll || d.IsAudioDevice)
            .ToList();

        // Keep the configured device visible even if it is currently filtered out or the
        // adapter cannot see it right now - otherwise saving would silently clear the target.
        if (Config.HasDevice && devices.All(d => d.Address != Config.DeviceAddressValue))
        {
            devices.Insert(0, new PairedDevice(
                Config.DeviceAddressValue,
                Config.DeviceName ?? PairedDevice.FormatAddress(Config.DeviceAddressValue),
                0, false, false, true));
        }

        DeviceCombo.ItemsSource = devices;
        DeviceCombo.SelectedItem = devices.FirstOrDefault(d => d.Address == Config.DeviceAddressValue);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await _controller.RefreshDevicesAsync();
            PopulateDevices();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_controller.Store.Directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _controller.Store.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _controller.ShowError($"Could not open the config folder: {ex.Message}");
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is PairedDevice device)
        {
            Config.DeviceAddress = device.AddressText;
            Config.DeviceName = device.DisplayName;
        }

        Config.ShowAllPairedDevices = ShowAllCheck.IsChecked == true;
        Config.EnableMissingProfilesOnConnect = EnableMissingProfilesCheck.IsChecked == true;
        Config.PlaySoundOnConnect = PlaySoundCheck.IsChecked == true;
        Config.NoActivate = NoActivateCheck.IsChecked == true;

        Config.PollIntervalMs = ParseInt(PollBox.Text, Config.PollIntervalMs);
        Config.ConnectTimeoutMs = ParseInt(ConnectTimeoutBox.Text, Config.ConnectTimeoutMs);
        Config.DisconnectTimeoutMs = ParseInt(DisconnectTimeoutBox.Text, Config.DisconnectTimeoutMs);
        Config.LinkRetryIntervalMs = ParseInt(RetryIntervalBox.Text, Config.LinkRetryIntervalMs);
        Config.SoundDelayMs = ParseInt(SoundDelayBox.Text, Config.SoundDelayMs);

        Config.WidgetSize = ParseDouble(SizeBox.Text, Config.WidgetSize);
        Config.IdleOpacity = ParseDouble(OpacityBox.Text, Config.IdleOpacity);
        Config.AccentColor = string.IsNullOrWhiteSpace(AccentBox.Text) ? "#4CC38A" : AccentBox.Text.Trim();

        List<string> services = ServicesBox.Text
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => Guid.TryParse(s, out _))
            .ToList();
        if (services.Count > 0) Config.ServiceGuids = services;

        // Startup lives in the registry, not the config file, so it is applied separately and
        // the config only mirrors the result.
        bool wantStartup = StartupCheck.IsChecked == true;
        if (wantStartup != StartupManager.IsEnabled())
        {
            string? error = StartupManager.SetEnabled(wantStartup);
            if (error is not null) _controller.ShowError(error);
        }
        Config.StartWithWindows = StartupManager.IsEnabled();

        Config.Normalize();
        _controller.Store.SaveNow();
        Close();
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;
}
