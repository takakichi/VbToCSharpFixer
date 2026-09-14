namespace VbToCSharpFixer;

/// <summary>XML項目とソース出力に共通の配置規則です。</summary>
internal static class ProjectPathMapper
{
    /// <summary>VBのMy Project配下にある標準ファイルをC#のProperties構成へ割り当てます。</summary>
    internal static string Map(string value)
    {
        // My Project全体を移動せず、C#の標準配置に対応するResources、Settings、manifestだけを移す。
        var normalized = value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar);
        if (parts.Length > 1 && parts[0].Equals("My Project", StringComparison.OrdinalIgnoreCase) &&
            (parts[1].StartsWith("Resources", StringComparison.OrdinalIgnoreCase) ||
             parts[1].StartsWith("Settings", StringComparison.OrdinalIgnoreCase) ||
             parts[1].Equals("app.manifest", StringComparison.OrdinalIgnoreCase)))
        {
            parts[0] = "Properties";
            return string.Join(Path.DirectorySeparatorChar, parts);
        }
        return normalized;
    }

}
