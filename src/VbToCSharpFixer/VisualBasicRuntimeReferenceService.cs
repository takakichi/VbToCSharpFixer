using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace VbToCSharpFixer;

public sealed class VisualBasicRuntimeReferenceService
{
    /// <summary>VBランタイム呼び出しを含む旧形式C#プロジェクトへ必要なアセンブリ参照を追加します。</summary>
    /// <param name="project">処理対象のプロジェクト。</param>
    /// <param name="projectOutputDirectory">変換後プロジェクトの出力ディレクトリ。</param>
    /// <param name="required">生成コードが参照するVBランタイム型一覧。</param>
    /// <param name="dryRun">ファイルを書き込まず処理内容だけを確認する場合はtrue。</param>
    /// <param name="cancellationToken">処理のキャンセル要求を通知するトークン。</param>
    /// <returns>追加した参照のログ。変更が不要な場合はnull。</returns>
    public async Task<ProjectConversionLogEntry?> EnsureReferenceAsync(
        Project project, string projectOutputDirectory, bool required, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (!required || project.FilePath is null) return null;
        var projectPath = Path.Combine(projectOutputDirectory,
            Path.ChangeExtension(Path.GetFileName(project.FilePath), ".csproj")!);
        if (dryRun)
            return new(project.Name, project.FilePath, projectPath,
                "Microsoft.VisualBasic reference required by converted runtime calls", "Planned");
        if (!File.Exists(projectPath)) return null;

        var document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        var root = document.Root!;
        if (root.Attribute("Sdk") is not null)
            return new(project.Name, project.FilePath, projectPath,
                "Microsoft.VisualBasic supplied by target framework", "AlreadyAvailable");
        if (root.Descendants().Any(x => x.Name.LocalName == "Reference" &&
            (x.Attribute("Include")?.Value.Split(',')[0].Trim().Equals("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase) ?? false)))
            return new(project.Name, project.FilePath, projectPath,
                "Microsoft.VisualBasic assembly reference", "AlreadyPresent");

        var ns = root.Name.Namespace;
        var itemGroup = root.Elements(ns + "ItemGroup")
            .FirstOrDefault(x => x.Elements(ns + "Reference").Any());
        if (itemGroup is null)
        {
            itemGroup = new XElement(ns + "ItemGroup");
            var firstImport = root.Elements(ns + "Import").FirstOrDefault();
            if (firstImport is null) root.Add(itemGroup); else firstImport.AddBeforeSelf(itemGroup);
        }
        itemGroup.Add(new XElement(ns + "Reference", new XAttribute("Include", "Microsoft.VisualBasic")));
        await using var stream = File.Create(projectPath);
        await document.SaveAsync(stream, SaveOptions.DisableFormatting, cancellationToken);
        return new(project.Name, project.FilePath, projectPath,
            "Added Microsoft.VisualBasic assembly reference for runtime compatibility", "Success");
    }
}
