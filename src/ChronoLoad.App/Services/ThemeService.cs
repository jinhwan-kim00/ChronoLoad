using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ChronoLoad.App.Services;

public enum AppTheme { System, Dark, Light }

/// <summary>
/// 테마 적용과 시스템 추종. 팔레트를 <c>Application.Resources</c>의 Brush 로 풀어 넣으므로
/// XAML 은 <c>DynamicResource</c> 만 쓰면 전환이 자동으로 따라온다.
/// </summary>
public sealed class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static ThemeService Instance { get; } = new();

    private AppTheme _mode = AppTheme.System;

    public ThemePalette Palette { get; private set; } = ThemePalette.Dark;

    public event Action? Changed;

    public AppTheme Mode
    {
        get => _mode;
        set { _mode = value; Apply(); }
    }

    /// <summary>수동 토글은 다크 ↔ 라이트만 오간다. 시스템 추종은 설정에서 고른다.</summary>
    public void Toggle() => Mode = Resolve() == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;

    public void Apply()
    {
        Palette = Resolve() == AppTheme.Light ? ThemePalette.Light : ThemePalette.Dark;

        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            Set(resources, "Brush.Bg", Palette.Bg);
            Set(resources, "Brush.Surface", Palette.Surface);
            Set(resources, "Brush.Surface2", Palette.Surface2);
            Set(resources, "Brush.Line", Palette.Line);
            Set(resources, "Brush.Fg", Palette.Fg);
            Set(resources, "Brush.Dim", Palette.Dim);
            Set(resources, "Brush.Faint", Palette.Faint);
            Set(resources, "Brush.Warn", Palette.Warn);
        }

        Changed?.Invoke();
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private AppTheme Resolve()
    {
        if (_mode != AppTheme.System) return _mode;
        return SystemPrefersLight() ? AppTheme.Light : AppTheme.Dark;
    }

    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;   // 읽을 수 없으면 다크로 둔다
        }
    }

    /// <summary>
    /// <c>WM_SETTINGCHANGE</c> 훅에서 호출한다. 시스템 추종 모드일 때만 반응한다.
    /// </summary>
    public void OnSystemSettingChanged()
    {
        if (_mode == AppTheme.System) Apply();
    }
}
