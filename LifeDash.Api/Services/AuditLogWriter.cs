using System.Text.Json;

namespace LifeDash.Api.Services;

public interface IAuditLogWriter
{
    void Write(object entry);
    string GetPath();
}

public sealed class AuditLogWriter(IConfiguration cfg, IWebHostEnvironment env) : IAuditLogWriter
{
    private static readonly object Sync = new();

    private readonly string _path = ResolvePath(cfg, env);

    public void Write(object entry)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        var line = JsonSerializer.Serialize(entry);
        lock (Sync)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    public string GetPath() => _path;

    private static string ResolvePath(IConfiguration cfg, IWebHostEnvironment env)
    {
        var configured = cfg["Logging:AuditPath"];
        if (string.IsNullOrWhiteSpace(configured)) configured = "App_Data/audit.log";
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(env.ContentRootPath, configured);
    }
}
