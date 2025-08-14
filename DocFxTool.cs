#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;

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
#if UNITY_2021_2_OR_NEWER
            var nbt = NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
            var defines = PlayerSettings.GetScriptingDefineSymbols(nbt);
            const string Flag = "FORCE_DOCFX_RECOMPILE";
            var parts = defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!parts.Contains(Flag)) parts.Add(Flag); else { parts.RemoveAll(d => d == Flag); parts.Add(Flag); }
            PlayerSettings.SetScriptingDefineSymbols(nbt, string.Join(";", parts));
#else
            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            const string Flag = "FORCE_DOCFX_RECOMPILE";
            var parts = defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!parts.Contains(Flag)) parts.Add(Flag); else { parts.RemoveAll(d => d == Flag); parts.Add(Flag); }
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", parts));
#endif
            AssetDatabase.Refresh();
            Debug.Log("DocFX: forcing full recompile…");
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
            var preserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".git", ".gitignore", "CNAME", "README.md", ".gitattributes" };

            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!preserve.Contains(name)) Directory.Delete(dir, true);
            }
            foreach (var file in Directory.GetFiles(root))
            {
                var name = Path.GetFileName(file);
                if (!preserve.Contains(name)) File.Delete(file);
            }
        }

        private static void CopyDirectoryContents(string sourceDir, string destDir, bool overwriteFiles)
        {
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = dir.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destDir, rel));
            }
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
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
                using var p = Process.Start(psi);
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();

                if (!string.IsNullOrEmpty(stdout)) Debug.Log(stdout);
                if (!string.IsNullOrEmpty(stderr)) Debug.LogWarning(stderr);

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
