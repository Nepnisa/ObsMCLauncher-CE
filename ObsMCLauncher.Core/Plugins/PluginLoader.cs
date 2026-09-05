using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ObsMCLauncher.Core.Utils;

namespace ObsMCLauncher.Core.Plugins;

public class PluginLoader
{
    private readonly string _pluginsDirectory;
    private readonly List<LoadedPlugin> _loadedPlugins = new();

    public static Action<string>? OnPluginDisabled { get; set; }
    public static Action<string>? OnPluginEnabled { get; set; }
    public static Action<string>? OnPluginRemoved { get; set; }

    public PluginLoader(string pluginsDirectory)
    {
        _pluginsDirectory = pluginsDirectory;

        // 社区版：直接创建目录但不加载任何插件
        if (!Directory.Exists(_pluginsDirectory))
        {
            Directory.CreateDirectory(_pluginsDirectory);
            DebugLogger.Info("PluginLoader", $"创建插件目录: {_pluginsDirectory}");
        }
        else
        {
            // 清空插件目录，防止残留
            try
            {
                foreach (var dir in Directory.GetDirectories(_pluginsDirectory))
                {
                    Directory.Delete(dir, true);
                }
                DebugLogger.Info("PluginLoader", "已清空插件目录");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn("PluginLoader", $"清空插件目录失败: {ex.Message}");
            }
        }

        // 写入一个说明文件
        try
        {
            var noticePath = Path.Combine(_pluginsDirectory, "README_社区版不支持插件.txt");
            File.WriteAllText(noticePath, 
                "ObsMCLauncher-CE 社区版不支持插件功能。\n\n" +
                "若想要使用插件，请前往官方版：\n" +
                "https://github.com/mcobs/ObsMCLauncher\n\n" +
                "本目录下的插件不会被加载。");
        }
        catch { }
    }

    public IReadOnlyList<LoadedPlugin> LoadedPlugins => _loadedPlugins.AsReadOnly();

    public void LoadAllPlugins()
    {
        DebugLogger.Info("PluginLoader", "社区版插件功能已禁用");
        _loadedPlugins.Clear();
    }

    public bool LoadPluginById(string pluginId)
    {
        DebugLogger.Warn("PluginLoader", $"社区版不支持加载插件: {pluginId}");
        return false;
    }

    public void UnloadAllPlugins()
    {
        DebugLogger.Info("PluginLoader", "社区版无需卸载插件");
        _loadedPlugins.Clear();
    }

    public void ShutdownPlugins()
    {
        DebugLogger.Info("PluginLoader", "社区版无需关闭插件");
    }

    public bool DisablePluginImmediately(string pluginId)
    {
        DebugLogger.Warn("PluginLoader", $"社区版不支持禁用插件: {pluginId}");
        return false;
    }

    public bool EnablePlugin(string pluginId, out string? errorMessage)
    {
        errorMessage = "社区版不支持启用插件，请使用官方版：https://github.com/mcobs/ObsMCLauncher";
        return false;
    }

    public bool RemovePlugin(string pluginId, out string? errorMessage)
    {
        errorMessage = "社区版不支持删除插件";
        return false;
    }

    // 以下私有方法保留但不会被调用，仅用于兼容性
    private static void CreateDisabledMarker(string pluginDirectory) { }
    private void CleanupMarkedPlugins() { }
    private string? ValidatePluginMetadata(PluginMetadata metadata, string pluginDirName) => "社区版禁用";
    private static int CompareVersions(string a, string b) => 0;
    private static int[] ParseVersionParts(string v) => [];
    private void LoadPlugin(string pluginDirectory) { }
}