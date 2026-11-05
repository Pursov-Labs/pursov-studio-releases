using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PursovStudio;

internal sealed class UiPalette : IDisposable
{
    private string mode = "";
    private bool disposed;
    public event Action? Changed;

    public UiPalette()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        SystemParameters.StaticPropertyChanged += SystemParameterChanged;
    }

    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueApply();
    private void SystemParameterChanged(object? sender, PropertyChangedEventArgs e) => QueueApply();
    private void QueueApply()
    {
        if (!disposed && !Application.Current.Dispatcher.HasShutdownStarted)
            Application.Current.Dispatcher.BeginInvoke(Apply);
    }

    private void Apply()
    {
        if (disposed) return;
        bool dark = false;
        try
        {
            using var personalization = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = personalization?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        string next = SystemParameters.HighContrast ? "contrast" : dark ? "dark" : "light";
        ApplyTheme(next);
    }

    internal void ApplyTheme(string next)
    {
        if (mode == next && next != "contrast") return;
        mode = next;
        bool dark = next == "dark";
        if (next == "contrast")
        {
            Set("Ground", SystemColors.WindowColor); Set("Surface", SystemColors.WindowColor);
            Set("Ink", SystemColors.WindowTextColor); Set("Muted", SystemColors.WindowTextColor);
            Set("Line", SystemColors.WindowTextColor); Set("Accent", SystemColors.WindowTextColor);
            Set("ActionFill", SystemColors.HighlightColor);
            Set("AccentText", SystemColors.HighlightTextColor); Set("Failure", SystemColors.WindowTextColor);
            Set("Warning", SystemColors.WindowTextColor);
        }
        else
        {
            Set("Ground", dark ? "#171C19" : "#F7F8F7");
            Set("Surface", dark ? "#202722" : "#FFFFFF");
            Set("Ink", dark ? "#EFF5F0" : "#18211C");
            Set("Muted", dark ? "#B6C2B9" : "#58635C");
            Set("Line", dark ? "#3A473E" : "#DCE2DD");
            Set("Accent", dark ? "#68D994" : "#176D3E");
            Set("ActionFill", dark ? "#68D994" : "#176D3E");
            Set("AccentText", dark ? "#10291A" : "#FFFFFF");
            Set("Failure", dark ? "#FFA69D" : "#AD302B");
            Set("Warning", dark ? "#E9C77D" : "#865C07");
        }
        Changed?.Invoke();
    }

    private static void Set(string name, string hex) => Set(name, (Color)ColorConverter.ConvertFromString(hex));
    private static void Set(string name, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[name] = brush;
    }

    public void Dispose()
    {
        disposed = true;
        SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameterChanged;
    }
}
