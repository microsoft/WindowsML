// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskStudio.Runtime;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TaskStudio;

public sealed partial class MainWindow
{
    private GpuDevice? selectedGpu;
    private bool devicesInitialized;

    private async Task InitializeGpuAsync()
    {
        if (devicesInitialized) return;
        devicesInitialized = true;
        SetBusy(true);
        try
        {
            var devices = await runtime.GetGpuDevicesAsync();
            if (devices.Count == 0) throw new NotSupportedException("No hardware GPU is available. This app requires a GPU that the CGC backend supports and does not fall back to the CPU.");
            DeviceSelector.ItemsSource = devices;
            DeviceSelector.SelectedIndex = 0;
            ShowStatus("GPU selected", $"{RequireGpu().Name}. Connect a model to create its Task session.", InfoBarSeverity.Informational);
        }
        catch (Exception error)
        {
            DeviceName.Text = "GPU unavailable";
            DeviceDetail.Text = "Check GPU drivers and restart the app.";
            ShowError(error);
        }
        finally { SetBusy(false); }
    }

    private GpuDevice RequireGpu() => selectedGpu ?? throw new InvalidOperationException("Select an available GPU first.");

    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceSelector.SelectedItem is not GpuDevice device) return;
        selectedGpu = device;
        ShowSelectedDevice();
        if (!busy) SetBusy(false);
    }

    private void ShowSelectedDevice()
    {
        var device = RequireGpu();
        DeviceName.Text = device.Name;
        DeviceDetail.Text = $"{(device.IsNvidia ? "NVIDIA preferred" : "Hardware GPU")} / Selected for new tasks\nRelease all sessions to change devices.";
    }

    private void ShowActiveDevice(GpuDevice device)
    {
        if (device.Identity != RequireGpu().Identity)
            throw new InvalidOperationException("The Runtime target does not match the selected GPU.");
        DeviceName.Text = device.Name;
        DeviceDetail.Text = $"Active Runtime target / CGC\nAdapter {device.Identity}";
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
