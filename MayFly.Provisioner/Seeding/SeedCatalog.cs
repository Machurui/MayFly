using System.Reflection;
using System.Text;

namespace MayFly.Provisioner.Seeding;

/// <summary>
/// Registry of built-in seed templates.  GetSql / GetMongoJs load embedded resources from this
/// assembly so the providers stay free of resource-loading boilerplate.
/// </summary>
public static class SeedCatalog
{
    public static readonly IReadOnlySet<string> Templates =
        new HashSet<string> { "northwind" };

    public static bool IsTemplate(string initialData) => Templates.Contains(initialData);

    /// <summary>Loads the embedded SQL seed for <paramref name="templateId"/>.
    /// Throws <see cref="InvalidOperationException"/> if the resource is absent.</summary>
    public static string GetSql(string templateId) =>
        LoadEmbedded($"{templateId}.sql");

    /// <summary>Loads the embedded MongoDB JS seed for <paramref name="templateId"/>.
    /// Throws <see cref="InvalidOperationException"/> if the resource is absent.</summary>
    public static string GetMongoJs(string templateId) =>
        LoadEmbedded($"{templateId}.mongo.js");

    private static string LoadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith(suffix));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
