using System;
using System.IO;
using Microsoft.Win32;

namespace DesktopShortcuts;

internal static class WindowsStartup
{
    internal const string MenuLabel = "Windows 시작 시 자동 실행";
    internal const string LaunchArgument = "--startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopShortcuts";

    internal static bool IsEnabled()
    {
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(run?.GetValue(ValueName) as string, GetLaunchCommand(), StringComparison.OrdinalIgnoreCase);
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string command = GetLaunchCommand();
        if (command.Length > 260)
        {
            throw new IOException("Windows 자동 실행에 등록하기에는 실행 파일 경로가 너무 깁니다.");
        }

        using RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        runKey.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string GetLaunchCommand()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("현재 실행 파일 경로를 확인할 수 없습니다.");
        return Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? $"\"{executable}\" \"{Path.Combine(AppContext.BaseDirectory, "DesktopShortcuts.dll")}\" {LaunchArgument}"
            : $"\"{executable}\" {LaunchArgument}";
    }
}
