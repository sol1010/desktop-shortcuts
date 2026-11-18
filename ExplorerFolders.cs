using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Microsoft.CSharp.RuntimeBinder;

namespace DesktopShortcuts;

internal enum FolderOpenMode { Default, NewTab, NewWindow }

internal static class ExplorerFolders
{
    private const string ExplorerWindowClass = "CabinetWClass";
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly SemaphoreSlim TabRequests = new(1, 1);

    public static bool SupportsTabs => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    public static ProcessStartInfo StartInfo(string path, FolderOpenMode mode) => new(path)
    {
        UseShellExecute = true,
        Verb = mode == FolderOpenMode.NewWindow ? "opennewwindow" : ""
    };

    public static async Task OpenTabAsync(string path)
    {
        await TabRequests.WaitAsync();
        try
        {
            nint explorer = FindExplorerWindow();
            if (explorer == 0)
            {
                using Process? process = Process.Start(StartInfo(path, FolderOpenMode.NewWindow));
                return;
            }

            Type type = Type.GetTypeFromCLSID(ShellWindowsClass, throwOnError: true)!;
            object shellWindows = Activator.CreateInstance(type)!;
            Dictionary<nint, object> existing = ReadTabs(shellWindows, explorer);

            if (!await Task.Run(() => AddTab(explorer)))
            {
                throw new InvalidOperationException("탐색기에서 새 탭 버튼을 찾을 수 없습니다.");
            }

            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(50);
                object? newTab = null;
                foreach (var (identity, tab) in ReadTabs(shellWindows, explorer))
                {
                    if (existing.ContainsKey(identity))
                    {
                        continue;
                    }

                    if (newTab is not null)
                    {
                        throw new InvalidOperationException("새로 열린 탭을 하나로 구분할 수 없습니다.");
                    }

                    newTab = tab;
                }

                if (newTab is not null)
                {
                    ((dynamic)newTab).Navigate2(path);
                    SetForegroundWindow(explorer);
                    return;
                }
            }

            throw new InvalidOperationException("새로운 탐색기 탭에 연결하지 못했습니다.");
        }
        finally
        {
            TabRequests.Release();
        }
    }

    private static bool AddTab(nint explorer)
    {
        if (IsIconic(explorer))
        {
            ShowWindowAsync(explorer, 9); // SW_RESTORE
        }

        AutomationElement root = AutomationElement.FromHandle(explorer);
        AutomationElement? button = root.FindFirst(TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "AddButton"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        if (button is null || !button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
        {
            return false;
        }

        ((InvokePattern)pattern).Invoke();
        return true;
    }

    private static Dictionary<nint, object> ReadTabs(object shellWindows, nint explorer)
    {
        var tabs = new Dictionary<nint, object>();
        dynamic windows = shellWindows;
        int count = windows.Count;
        for (int index = 0; index < count; index++)
        {
            try
            {
                object? tab = windows.Item(index);
                if (tab is null)
                {
                    continue;
                }

                nint handle = new((long)((dynamic)tab).HWND);
                if (handle != explorer && GetAncestor(handle, 2) != explorer) // GA_ROOT
                {
                    continue;
                }

                // Retain the COM objects so the identities of existing tabs stay valid.
                nint identity = Marshal.GetIUnknownForObject(tab);
                try
                {
                    tabs.TryAdd(identity, tab);
                }
                finally
                {
                    Marshal.Release(identity);
                }
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException)
            {
                // The shell window collection can change while we enumerate it.
            }
        }

        return tabs;
    }

    private static nint FindExplorerWindow()
    {
        nint window = 0;
        while ((window = FindWindowEx(0, window, ExplorerWindowClass, null)) != 0)
        {
            if (IsWindowVisible(window))
            {
                return window;
            }
        }

        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? name);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flag);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
