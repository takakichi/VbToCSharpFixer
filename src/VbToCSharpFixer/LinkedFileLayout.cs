using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

namespace VbToCSharpFixer;

/// <summary>Linkの表示名ではなく元の実パスから、全プロジェクト共通の出力先を決めます。</summary>
internal sealed class LinkedFileLayout(Options options, OutputLayout layout, IReadOnlyList<LoadedProject> projects)
{
    internal string Destination(string source)
    {
        source = Path.GetFullPath(source);
        var owner = projects.Select(p => p.Project).Where(p => p.FilePath is not null)
            .OrderByDescending(p => Path.GetDirectoryName(p.FilePath!)!.Length)
            .FirstOrDefault(p => OutputLayout.IsWithin(Path.GetDirectoryName(p.FilePath!)!, source));
        var converted = Path.GetExtension(source).Equals(".vb", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(source, ".cs") : source;
        if (owner is not null)
            return layout.PathInProject(owner, Path.GetRelativePath(Path.GetDirectoryName(owner.FilePath!)!, converted));
        if (options.Solution is not null && OutputLayout.IsWithin(Path.GetDirectoryName(options.Solution)!, source))
            return Path.Combine(layout.ConversionRoot, Path.GetRelativePath(Path.GetDirectoryName(options.Solution)!, converted));
        // ソリューション外でも一つの共有先に置く。同名ファイルの衝突を避け、Form一式は同じフォルダーへ。
        var directory = Path.GetDirectoryName(source)!;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
        return Path.Combine(layout.OutputBase, "_linked", key, Path.GetFileName(converted));
    }
}
