using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using Newtonsoft.Json.Linq;

namespace DWSIM.UI.Desktop.Avalonia;

public partial class ScriptEditorWindow
{
    private string _preferredFont = "Hack";
    private readonly Dictionary<string, IHighlightingDefinition> _darkDefinitions = new();
    private readonly Dictionary<string, WorkingCopy> _workingCopies = new();
    private readonly string _zedSession = Guid.NewGuid().ToString("N");

    private sealed record WorkingCopy(string Path, string LastImportedText);

    private void ApplyZedEditorPreferences()
    {
        // Zed settings use JSON with comments. Read only editor font preferences; never modify them.
        try
        {
            var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configRoot))
                configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var settings = JObject.Parse(File.ReadAllText(Path.Combine(configRoot, "zed", "settings.json")));
            if (settings["buffer_font_family"]?.Value<string>() is { Length: > 0 } font)
                _preferredFont = font;
            if (settings["buffer_font_size"]?.Value<double>() is double size && double.IsFinite(size) && size >= 8 && size <= 72)
                ScriptEditor.FontSize = size;
        }
        catch (Exception) { /* Zed is optional; keep readable defaults. */ }

        ScriptEditor.FontFamily = new FontFamily(_preferredFont + ",DejaVu Sans Mono,monospace");
        ScriptEditor.TextArea.Caret.CaretBrush = Brush.Parse("#FFCC66");
        ScriptEditor.TextArea.SelectionBrush = Brush.Parse("#33415E");
        ScriptEditor.TextArea.SelectionForeground = Brush.Parse("#BFBDB6");
        ScriptEditor.TextArea.TextView.CurrentLineBackground = Brush.Parse("#131721");
        ScriptEditor.Options.HighlightCurrentLine = true;
        ScriptEditor.SyntaxHighlighting = GetDarkHighlighting("Python-Mode.xshd");
    }

    private IHighlightingDefinition? GetDarkHighlighting(string resourceName)
    {
        if (_darkDefinitions.TryGetValue(resourceName, out var definition)) return definition;
        // Load a private copy so the application's other editors keep their own highlighting.
        using var stream = typeof(HighlightingManager).Assembly.GetManifestResourceStream(
            "AvaloniaEdit.Highlighting.Resources." + resourceName);
        if (stream == null) return null;
        var xml = XDocument.Load(stream);
        foreach (var color in xml.Descendants().Where(e => e.Name.LocalName == "Color"))
        {
            var name = ((string?)color.Attribute("name") ?? "").ToLowerInvariant();
            var foreground = name.Contains("comment") ? "#ACB6BF"
                : name.Contains("string") || name.Contains("char") ? "#AAD94C"
                : name.Contains("number") || name.Contains("digit") || name.Contains("literal") ? "#D2A6FF"
                : name.Contains("keyword") || name.Contains("control") || name.Contains("modifier") ? "#FF8F40"
                : name.Contains("type") || name.Contains("class") ? "#59C2FF"
                : name.Contains("method") || name.Contains("function") ? "#FFB454"
                : "#BFBDB6";
            color.SetAttributeValue("foreground", foreground);
            color.Attribute("background")?.Remove();
        }
        using var reader = xml.CreateReader();
        definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        _darkDefinitions[resourceName] = definition;
        return definition;
    }

    private static string? FindZed()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "zed.exe" } : new[] { "zeditor", "zed" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private void EditInZed()
    {
        var script = _current;
        if (script == null || ScriptEditor.IsReadOnly) return;
        try
        {
            var executable = FindZed();
            if (executable == null)
            {
                SetStatus("Zed CLI not found. Install Zed and add zeditor or zed to PATH.");
                return;
            }
            var text = script.ScriptText ?? "";
            if (_workingCopies.TryGetValue(script.ID, out var existing))
            {
                if (File.Exists(existing.Path) && File.ReadAllText(existing.Path) != existing.LastImportedText)
                {
                    LaunchZed(executable, existing.Path);
                    SetStatus("Reopened Zed's working copy; import its saved changes before exporting again.");
                    return;
                }
            }
            var extension = script.PythonInterpreter == Scripts.Interpreter.Julia ? ".jl" : ".py";
            var directory = Path.Combine(Path.GetTempPath(), "dwsim-zed", _zedSession);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Convert.ToHexString(Encoding.UTF8.GetBytes(script.ID)) + extension);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            _workingCopies[script.ID] = new WorkingCopy(path, text);
            LaunchZed(executable, path);
            SetStatus("Editing in Zed. Save there, then click Import from Zed. The flowsheet has not been saved.");
        }
        catch (Exception ex)
        {
            SetStatus("Cannot open Zed: " + ex.Message);
        }
    }

    private static void LaunchZed(string executable, string path)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--existing");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start);
    }

    private async Task ImportFromZedAsync()
    {
        var script = _current;
        if (script == null || ScriptEditor.IsReadOnly) return;
        if (!_workingCopies.TryGetValue(script.ID, out var copy))
        {
            SetStatus("Click Edit in Zed first, then save the working copy in Zed.");
            return;
        }
        try
        {
            var text = File.ReadAllText(copy.Path);
            if (text == copy.LastImportedText)
            {
                SetStatus("No saved changes in Zed. Save the working copy there first.");
                return;
            }
            if ((script.ScriptText ?? "") != copy.LastImportedText && script.ScriptText != text)
            {
                if (!await ConfirmAsync("Script changed in both editors",
                    "The flowsheet script and Zed working copy have both changed. Replace the flowsheet script with Zed's saved version?")) return;
                if (_current != script || ScriptEditor.IsReadOnly) return;
                // The Zed file may have changed while the confirmation was open.
                if (File.ReadAllText(copy.Path) != text)
                {
                    SetStatus("Zed's file changed during confirmation. Import again to review the latest version.");
                    return;
                }
            }
            ScriptEditor.Text = text;
            _workingCopies[script.ID] = copy with { LastImportedText = text };
            SetStatus("Imported from Zed. Save the flowsheet to persist the script; nothing has been executed.");
        }
        catch (Exception ex) { SetStatus("Cannot import from Zed: " + ex.Message); }
    }
}
