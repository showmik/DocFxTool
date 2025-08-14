#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
#if UNITY_2021_2_OR_NEWER
using UnityEditor.Build;
#endif
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;
using System.Reflection;

namespace TTT.Editor
{
    public static class DocFxTool
    {
        // ------- 1) Configure XML (per-asmdef csc.rsp) -------
        [MenuItem("Tools/DocFX Tool/1) Configure XML (csc.rsp per asmdef)")]
        public static void EnsureCscRspPerAsmdef()
        {
            var asmdefFiles = Directory.GetFiles(Application.dataPath, "*.asmdef", SearchOption.AllDirectories);
            int created = 0, updated = 0, skipped = 0;

            foreach (var asmdef in asmdefFiles)
            {
                var asmName = Path.GetFileNameWithoutExtension(asmdef);
                var asmDir = Path.GetDirectoryName(asmdef)!;
                var rspPath = Path.Combine(asmDir, "csc.rsp");
                var docPath = $"Library/ScriptAssemblies/{asmName}.xml";

                var lines = File.Exists(rspPath) ? File.ReadAllLines(rspPath).ToList() : new List<string>();
                bool hadDoc = lines.Any(l => l.Contains("-doc:"));
                bool hadNowarn = lines.Any(l => l.StartsWith("-nowarn:", StringComparison.OrdinalIgnoreCase));

                if (!hadDoc) lines.Add($"-doc:\"{docPath}\"");
                if (!hadNowarn) lines.Add("-nowarn:1591");

                if (!File.Exists(rspPath)) { File.WriteAllLines(rspPath, lines); created++; }
                else if (!hadDoc || !hadNowarn) { File.WriteAllLines(rspPath, lines); updated++; }
                else skipped++;
            }

            var globalRsp = Path.Combine(Application.dataPath, "csc.rsp");
            if (File.Exists(globalRsp) && File.ReadAllText(globalRsp).Contains("-doc:"))
                Debug.LogWarning("DocFX: Found Assets/csc.rsp with -doc:. This can override per-asmdef outputs.");

            AssetDatabase.Refresh();
            Debug.Log($"DocFX: csc.rsp done. Created:{created} Updated:{updated} Unchanged:{skipped}");
        }

        // ------- 2) Recompile all (emit XML now) -------
        [MenuItem("Tools/DocFX Tool/2) Recompile all (emit XML now)")]
        public static void ForceFullRecompile()
        {
            const string Prefix = "FORCE_DOCFX_RECOMPILE";
            string flag = $"{Prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}"; // always new -> forces recompile

#if UNITY_2021_2_OR_NEWER
            var targets = new HashSet<NamedBuildTarget>();

            // Always include Standalone and the currently selected group
            targets.Add(NamedBuildTarget.Standalone);
            targets.Add(NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup));

            // Try to include the Editor named target if this Unity version has it
            var editorProp = typeof(NamedBuildTarget).GetProperty("Editor", BindingFlags.Public | BindingFlags.Static);
            if (editorProp != null)
                targets.Add((NamedBuildTarget)editorProp.GetValue(null));

            foreach (var nbt in targets)
            {
                var defines = PlayerSettings.GetScriptingDefineSymbols(nbt);
                var parts = defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Where(d => !d.StartsWith(Prefix, StringComparison.Ordinal))
                                   .ToList();
                parts.Add(flag);
                PlayerSettings.SetScriptingDefineSymbols(nbt, string.Join(";", parts));
            }
#else
    var groups = new HashSet<BuildTargetGroup>
    {
        BuildTargetGroup.Standalone,
        EditorUserBuildSettings.selectedBuildTargetGroup
    };

    foreach (var g in groups.Where(g => g != BuildTargetGroup.Unknown))
    {
        var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(g);
        var parts = defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                           .Where(d => !d.StartsWith(Prefix, StringComparison.Ordinal))
                           .ToList();
        parts.Add(flag);
        PlayerSettings.SetScriptingDefineSymbolsForGroup(g, string.Join(";", parts));
    }
#endif

            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            Debug.Log($"DocFX: forced full recompile via define '{flag}'.");
        }



        // ------- 3) Build DocFX (metadata + build) -------
        [MenuItem("Tools/DocFX Tool/3) Build DocFX (metadata + build)")]
        public static void BuildDocfx()
        {
            string docsDir = Path.Combine(GetProjectRoot(), "Docs");
            string config = Path.Combine(docsDir, "docfx.json");
            if (!File.Exists(config))
            {
                EditorUtility.DisplayDialog("DocFX", $"docfx.json not found:\n{config}", "OK");
                return;
            }

            string docfxExe = ResolveDocfxExecutable();
            if (string.IsNullOrEmpty(docfxExe))
            {
                EditorUtility.DisplayDialog("DocFX", "Could not locate 'docfx'. Install with: dotnet tool install -g docfx", "OK");
                return;
            }

            bool ok1 = RunProcess(docfxExe, $"metadata \"{config}\"", docsDir);
            bool ok2 = ok1 && RunProcess(docfxExe, $"build \"{config}\"", docsDir);

            if (ok2)
            {
                Debug.Log("DocFX: build complete. (_site ready)");
                string nojekyll = Path.Combine(docsDir, "_site", ".nojekyll");
                if (!File.Exists(nojekyll)) File.WriteAllText(nojekyll, string.Empty);
            }
            else
            {
                Debug.LogError("DocFX: build failed. See Console for details.");
            }
        }

        // ------- 4) Export site → Docs/<Project>-doc-site (git-safe, no push) -------
        [MenuItem("Tools/DocFX Tool/4) Export site → Docs/<Project>-doc-site")]
        public static void ExportSite()
        {
            if (TryExportSite(out var destDir))
            {
                EditorUtility.RevealInFinder(destDir);
                EditorUtility.DisplayDialog("DocFX Export", $"Export complete.\n\n{destDir}", "OK");
            }
        }

        // ------- 5) Show XML status -------
        [MenuItem("Tools/DocFX Tool/5) Show XML status")]
        public static void ShowXmlStatus()
        {
            var asmdefFiles = Directory.GetFiles(Application.dataPath, "*.asmdef", SearchOption.AllDirectories);
            var missing = asmdefFiles.Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(name => !File.Exists(Path.Combine("Library/ScriptAssemblies", $"{name}.xml")))
                .ToList();

            if (missing.Count == 0)
                EditorUtility.DisplayDialog("DocFX XML Status", "✅ All assemblies have XML docs.", "OK");
            else
                EditorUtility.DisplayDialog("DocFX XML Status", "❌ Missing XML for:\n" + string.Join("\n", missing), "OK");
        }

        // ------- 6) Open ScriptAssemblies -------
        [MenuItem("Tools/DocFX Tool/6) Open ScriptAssemblies folder")]
        public static void OpenScriptAssemblies()
        {
            EditorUtility.RevealInFinder(Path.Combine(Directory.GetCurrentDirectory(), "Library/ScriptAssemblies"));
        }

        // ===== internals =====

        private static bool TryExportSite(out string destDir)
        {
            string projectRoot = GetProjectRoot();
            string projectName = new DirectoryInfo(projectRoot).Name;
            string docsDir = Path.Combine(projectRoot, "Docs");
            string srcSite = Path.Combine(docsDir, "_site");
            destDir = Path.Combine(docsDir, $"{projectName}-doc-site");

            if (!Directory.Exists(srcSite))
            {
                EditorUtility.DisplayDialog("DocFX Export", $"Source not found:\n{srcSite}\n\nRun 'Build DocFX' first.", "OK");
                return false;
            }

            bool destExists = Directory.Exists(destDir);
            bool isGitRepo = destExists && Directory.Exists(Path.Combine(destDir, ".git"));

            if (destExists)
            {
                if (isGitRepo)
                {
                    bool update = EditorUtility.DisplayDialog(
                        "Update existing site repo?",
                        "This keeps .git, .gitignore, CNAME, README.md, .gitattributes and replaces everything else.",
                        "Update", "Cancel");
                    if (!update) return false;
                    SafeCleanDirectoryPreservingGit(destDir);
                }
                else
                {
                    bool replace = EditorUtility.DisplayDialog("Replace existing folder?",
                        $"This will delete and recreate:\n{destDir}", "Replace", "Cancel");
                    if (!replace) return false;
                    try { Directory.Delete(destDir, true); } catch { }
                    Directory.CreateDirectory(destDir);
                }
            }
            else
            {
                Directory.CreateDirectory(destDir);
            }

            try
            {
                CopyDirectoryContents(srcSite, destDir, overwriteFiles: true);
                string nojekyll = Path.Combine(destDir, ".nojekyll");
                if (!File.Exists(nojekyll)) File.WriteAllText(nojekyll, string.Empty);
                return true;
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("DocFX Export", $"Copy failed:\n{ex.Message}", "OK");
                return false;
            }
        }

        private static string GetProjectRoot()
        {
            var assets = Application.dataPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.GetDirectoryName(assets) ?? Directory.GetCurrentDirectory();
        }

        private static void SafeCleanDirectoryPreservingGit(string root)
        {
            var rootFull = Path.GetFullPath(root);
            var docsFull = Path.GetFullPath(Path.Combine(GetProjectRoot(), "Docs"));
            if (!rootFull.StartsWith(docsFull, StringComparison.OrdinalIgnoreCase) ||
                !rootFull.EndsWith("-doc-site", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to clean suspicious path: {rootFull}");
            }

            var preserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".git", ".gitignore", "CNAME", "README.md", ".gitattributes" };

            foreach (var dir in Directory.GetDirectories(root))
                if (!preserve.Contains(Path.GetFileName(dir))) Directory.Delete(dir, true);

            foreach (var file in Directory.GetFiles(root))
                if (!preserve.Contains(Path.GetFileName(file))) File.Delete(file);
        }


        private static bool IsLink(FileSystemInfo f) =>
    (f.Attributes & FileAttributes.ReparsePoint) != 0;

        private static void CopyDirectoryContents(string sourceDir, string destDir, bool overwriteFiles)
        {
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var di = new DirectoryInfo(dir);
                if (IsLink(di)) continue;
                var rel = dir.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destDir, rel));
            }
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(file);
                if (IsLink(fi)) continue;
                var rel = file.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var target = Path.Combine(destDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwriteFiles);
            }
        }

        private static string ResolveDocfxExecutable()
        {
#if UNITY_EDITOR_WIN
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string candidate = Path.Combine(home, ".dotnet", "tools", "docfx.exe");
            if (File.Exists(candidate)) return candidate;
            return "docfx";
#else
            return "docfx";
#endif
        }

        private static bool RunProcess(string fileName, string args, string workingDir)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var stdout = new System.Text.StringBuilder();
                var stderr = new System.Text.StringBuilder();

                using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();

                var outStr = stdout.ToString();
                var errStr = stderr.ToString();
                if (!string.IsNullOrEmpty(outStr)) Debug.Log(outStr);
                if (!string.IsNullOrEmpty(errStr)) Debug.LogWarning(errStr);

                if (p.ExitCode != 0)
                {
                    Debug.LogError($"Process '{fileName} {args}' failed with exit code {p.ExitCode}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start process '{fileName} {args}': {ex.Message}");
                return false;
            }
        }

    }
}
#endif
