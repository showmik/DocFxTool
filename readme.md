
# DocFxTool for Unity

Unity Editor tool that makes **[DocFX](https://dotnet.github.io/docfx/)** docs fast and repeatable:
- configures **XML docs per asmdef** (`csc.rsp`)
- forces a **full recompile** so all XMLs generate together
- runs **`docfx metadata` + `docfx build`**
- **exports** the static site to `Docs/<ProjectName>-doc-site` (git-safe; preserves `.git` if present)

---

## Why document?
- **Searchable API** for your systems  
- **Faster onboarding** and safer refactors  
- **Shareable reference** (internal or public)

## What problem does it solve?
Unity sometimes re-compiles only one assembly and “cleans” others → you end up with **only one** XML file, so DocFX shows no member summaries. This tool fixes the pipeline end-to-end and gives you a clean, exportable site folder.

---

## Requirements

- Unity 2019.4+ (tested on 2021/2022/2023)
- [.NET SDK 8.x](https://dotnet.microsoft.com/download) installed
- DocFX (2.x) installed as a global tool:
  ```powershell
  dotnet tool install -g docfx
  ```

---

## Install

1. Copy `DocFxTool.cs` into an **`Editor/`** folder (e.g. `Assets/_Project/_Scripts/Editor/DocFxTool.cs`).
2. Install DocFX:
	```powershell
	dotnet tool install -g docfx
	```
3. Put your **DocFX config** at `Docs/docfx.json`.

> Unity 2021.2+ uses `NamedBuildTarget` APIs; older Unity falls back automatically.

---

## Usage (happy path)

1. **Tools → DocFX Tool → 1) Configure XML**
2. **Tools → DocFX Tool → 2) Recompile all** (wait for compile)
3. **Tools → DocFX Tool → 3) Build DocFX** (creates `Docs/_site`)
4. **Tools → DocFX Tool → 4) Export site** → `Docs/<Project>-doc-site`

Serve locally (optional):

```powershell
cd Docs
docfx serve _site -p 8081
```

---

## Publish your site (manual—tool does not commit/push)

### Option A — GitHub Pages (branch: `gh-pages`)

1. Create a new empty repo on GitHub (e.g. `taptech-tycoon-docs`).
2. From your Unity project:

   ```powershell
   cd Docs/<ProjectName>-doc-site
   git init
   git add -A
   git commit -m "Publish site"
   git branch -M gh-pages
   git remote add origin https://github.com/showmik/taptech-tycoon-docs.git
   git push -u origin gh-pages
   ```
3. GitHub → **Settings → Pages** → Source: **`gh-pages`** / **root**.

Your site: `https://showmik.github.io/taptech-tycoon-docs/`

### Option B — Netlify (no build needed)

* **Drag-and-drop:** upload the `Docs/<ProjectName>-doc-site` folder.
* **Or connect the repo:** Build command: **none**, Publish directory: **/** (root).

---

## Minimal `docfx.json` (Unity assemblies mode)

```json
{
  "metadata": [
    {
      "src": [
        { "src": "../Library/ScriptAssemblies",
          "files": [ "YourGame.Core.dll", "YourGame.Gameplay.dll", "YourGame.UI.dll" ] }
      ],
      "dest": "api",
      "filter": "filterConfig.yml"
    }
  ],
  "build": {
    "content": [
      { "files": [ "index.md", "articles/**.md", "api/**.md", "toc.yml" ] },
      { "files": [ "api/**.yml" ] }
    ],
    "template": [ "default" ],
    "dest": "_site",
    "globalMetadata": { "_appTitle": "YourGame — API", "_enableSearch": true },
    "xref": []
  }
}
```

Add your custom theme via `"template": ["default", "templates/your-theme"]` if needed.
