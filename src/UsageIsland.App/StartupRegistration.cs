using System;
using System.IO;
using Microsoft.Win32;

namespace UsageIsland.App;

public sealed class StartupRegistration(string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", string valueName = "CloudPulseUsageIsland")
{
    public string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
    public static string BuildCommand(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) throw new ArgumentException("自启动需要有效的程序绝对路径。");
        var command = "\"" + Path.GetFullPath(executablePath) + "\" --autostart";
        if (command.Length > 260) throw new ArgumentException("程序路径过长，请将软件放到较短的固定目录后再开启自启动。");
        return command;
    }
    public void SetEnabled(bool enabled, string executablePath)
    {
        if (!enabled) { WriteCommand(null); return; }
        var command = BuildCommand(executablePath);
        if (!File.Exists(executablePath)) throw new ArgumentException("找不到当前程序，请先将软件解压到固定目录。");
        WriteCommand(command);
    }
    private void WriteCommand(string? command)
    {
        if (command is null) { using var key = Registry.CurrentUser.OpenSubKey(keyPath, true); key?.DeleteValue(valueName, false); }
        else { using var key = Registry.CurrentUser.CreateSubKey(keyPath); key.SetValue(valueName, command, RegistryValueKind.String); }
    }
    public void Apply(bool enabled, string executablePath, Action saveConfiguration)
    {
        var before = ReadCommand();
        try { SetEnabled(enabled, executablePath); saveConfiguration(); }
        catch { WriteCommand(before); throw; }
    }
}
