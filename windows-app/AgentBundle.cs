using System.IO;
using System.Reflection;
using System.Text;
using PiSignage.Signage;

namespace PiSignage.Control;

/// <summary>The agent software that shipped inside this exe (embedded at build
/// time from ..\agent). Shipping a new exe is how the client gets Pi updates.</summary>
public static class AgentBundle
{
    static readonly HashSet<string> AllowedRootFiles = new(StringComparer.Ordinal)
    {
        "main.py",
        "trust.py",
        "control_auth.py",
        "delivery_reset.py",
    };

    public static Dictionary<string, byte[]> Files()
    {
        var asm = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, byte[]>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("agent/")) continue;
            var path = name.Substring("agent/".Length).Replace('\\', '/');
            if (!path.StartsWith("static/", StringComparison.Ordinal) &&
                !AllowedRootFiles.Contains(path))
            {
                continue;
            }
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            files[path] = ms.ToArray();
        }
        return files;
    }

    public static string? Version()
    {
        var files = Files();
        return files.TryGetValue("main.py", out var main)
            ? AgentUpdater.ParseVersion(Encoding.UTF8.GetString(main))
            : null;
    }
}
