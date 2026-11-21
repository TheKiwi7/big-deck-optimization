using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Script <c>savefiles [label]</c> (savecheck-on/off): the local run save (<c>current_run.save</c> of the current profile) and its
/// <c>.backup</c> as STATE lines, which compare.py checks line for line between two sessions: present or not, whole JSON or not,
/// and a hash of its content with every property whose name holds "time" left out (save, start and run times differ between
/// two sessions of the same run). Then the count of the game's own "Not copying ... it is not a whole save" warnings since the
/// first <c>savefiles</c> line of the session (put that line before the run starts). What SaveBackupCheck could change, the
/// backup's existence and content, is compared there; runstate can't see it.
/// </summary>
public static partial class AutoTest
{
    private static int _notWholeWarnings;
    private static bool _notWholeCounted;

    [ScriptCommand("savefiles")]
    private static void SaveFiles(SceneTree tree, string arg)
    {
        if (!_notWholeCounted)
        {
            _notWholeCounted = true;
            Log.LogCallback += (level, text, _) =>
            {
                if (level == LogLevel.Warn && text.Contains("Not copying ", StringComparison.Ordinal) && text.Contains("it is not a whole save", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _notWholeWarnings);
                }
            };
        }
        string label = arg.Trim().Length > 0 ? arg.Trim() : "now";
        if (!SaveManager.Instance.IsProfileInitialized)
        {
            Say($"  STATE savefiles {label}: no profile yet, not-whole warnings {Volatile.Read(ref _notWholeWarnings)}");
            return;
        }
        string path = UserDataPathProvider.GetAccountScopedBasePath(null) + "/" + RunSaveManager.GetRunSavePath(SaveManager.Instance.CurrentProfileId, "current_run.save");
        Say($"  STATE savefiles {label}: current_run.save {SaveFileText(path)}; .backup {SaveFileText(path + ".backup")}; not-whole warnings {Volatile.Read(ref _notWholeWarnings)}");
    }

    /// <summary>"absent", or whole/not whole with a hash of its content (times left out when whole, the raw bytes when not).</summary>
    private static string SaveFileText(string path)
    {
        if (!Godot.FileAccess.FileExists(path))
        {
            return "absent";
        }
        byte[] bytes = Godot.FileAccess.GetFileAsBytes(path);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(bytes);
        }
        catch (System.Text.Json.JsonException)
        {
            return $"not whole, {bytes.Length} bytes, raw {Hash(bytes)}";
        }
        DropTimes(node);
        return $"whole, content {Hash(System.Text.Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null"))}";
    }

    private static void DropTimes(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(p => p.Key).Where(k => k.Contains("time", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    obj.Remove(key);
                }
                foreach (var property in obj)
                {
                    DropTimes(property.Value);
                }
                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    DropTimes(item);
                }
                break;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];
}
