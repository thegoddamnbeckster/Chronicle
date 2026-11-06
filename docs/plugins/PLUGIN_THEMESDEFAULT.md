# Default Themes plugin

**Version:** 1.0.0  ·  **Repository:** `Chronicle.Plugin.Themes.Default`  ·  **Media types:** n/a

**What it needs from the owner:** None. Four built-in colour themes.

**The source's terms and limits:** Ships with Chronicle.

The plugin's own README follows; the repository is the source of truth.

---

# Chronicle.Plugin.Themes.Default

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.Themes.Default?style=flat-square&label=release)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Themes.Default/releases/latest)

The default theme pack for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle). Provides four built-in themes — **Light**, **Dark**, **Navy & Pink**, and **Dark Teal** — via Chronicle's `IThemePlugin` interface.

Because themes are loaded from plugins at runtime, you can create your own theme pack and install it alongside this one. All themes from all installed theme plugins appear together in the Chronicle theme picker.

---

## Contents

- [How the theme system works](#how-the-theme-system-works)
- [Creating your own theme plugin](#creating-your-own-theme-plugin)
  - [Prerequisites](#prerequisites)
  - [1. Create the project](#1-create-the-project)
  - [2. Implement IThemePlugin](#2-implement-ithemeplugin)
  - [3. Define your themes](#3-define-your-themes)
  - [4. The CSS variable contract](#4-the-css-variable-contract)
  - [5. Write the manifest](#5-write-the-manifest)
  - [6. Build and deploy](#6-build-and-deploy)
- [Tips and conventions](#tips-and-conventions)
- [Reference: all CSS variables](#reference-all-css-variables)

---

## How the theme system works

Chronicle loads every DLL in its `plugins/` directory at startup. Any class that implements `IThemePlugin` is automatically discovered and registered. When the frontend loads, it calls `GET /api/v1/themes` — an unauthenticated endpoint that aggregates themes from every registered `IThemePlugin` and returns them as a list.

The user picks a theme in **Settings → Plugins** or **Settings → Preferences**. Chronicle applies the theme by setting each CSS custom property directly on `document.documentElement.style`. The variable map is cached in `localStorage` so the correct theme is applied before React even mounts, preventing any flash of the wrong theme on page load.

Multiple theme plugins coexist peacefully — all their themes appear in the same list. There is no concept of a "primary" plugin; order depends on the order plugins were registered at startup.

---

## Creating your own theme plugin

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- A local clone of the [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) repository (needed to reference `Chronicle.Plugins.csproj` during development)

> **Note:** Once `Chronicle.Plugins` is published to NuGet the local clone won't be required. Until then, keep your plugin repo as a sibling of the Chronicle repo (e.g. `W:\Scripts\Chronicle` and `W:\Scripts\Chronicle.Plugin.Themes.Monokai`).

---

### 1. Create the project

```bash
mkdir Chronicle.Plugin.Themes.YourName
cd Chronicle.Plugin.Themes.YourName
dotnet new classlib -f net9.0 -n Chronicle.Plugin.Themes.YourName
```

Edit the generated `.csproj` to match this structure — the key points are the `Private="false"` reference (so `Chronicle.Plugins.dll` is not copied into your output; Chronicle provides it at runtime) and the `manifest.json` copy rule:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>Chronicle.Plugin.Themes.YourName</AssemblyName>
    <RootNamespace>Chronicle.Plugin.Themes.YourName</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <!-- Local path reference during development.
         Adjust the relative path to match your directory layout.
         Replace with a NuGet reference once Chronicle.Plugins is published. -->
    <ProjectReference
      Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
      Private="false"
      ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
    <None Include="manifest.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

---

### 2. Implement IThemePlugin

Create your plugin class. It must implement `Chronicle.Plugins.IThemePlugin`:

```csharp
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Themes.YourName;

public sealed class YourNameThemesPlugin : IThemePlugin
{
    // Must be a unique reverse-domain identifier.
    // Convention: chronicle.plugin.themes.<yourname>
    public string PluginId => "chronicle.plugin.themes.yourname";

    public string Name    => "Your Name Themes";
    public string Version => "1.0.0";
    public string Author  => "Your Name";

    public IReadOnlyList<ThemeDefinition> GetThemes() => AllThemes;

    // Define your themes below (see section 3).
    // Declare AllThemes AFTER all theme fields so C# static
    // initialisation order is correct.
    private static readonly IReadOnlyList<ThemeDefinition> AllThemes =
    [
        MyTheme,
        AnotherTheme,
    ];
}
```

**Interface contract:**

| Member | Type | Description |
|--------|------|-------------|
| `PluginId` | `string` | Reverse-domain unique ID. Used as the namespace for theme storage keys in user preferences. |
| `Name` | `string` | Display name shown in the Installed Plugins list. |
| `Version` | `string` | Semantic version string. |
| `Author` | `string` | Your name or organisation. |
| `GetThemes()` | `IReadOnlyList<ThemeDefinition>` | Returns all themes this plugin provides. Called on every `GET /api/v1/themes` request — keep it allocation-free (use a `static readonly` list). |

---

### 3. Define your themes

Each theme is a `ThemeDefinition` record:

```csharp
private static readonly ThemeDefinition MyTheme = new(
    Key:         "monokai",           // machine-readable, unique within your plugin
    Label:       "Monokai",           // shown in the theme picker
    Description: "Classic Monokai colour scheme.",
    Swatches:    ["#272822", "#f92672", "#75715e"],  // exactly 3: [background, accent, midtone]
    Variables: new Dictionary<string, string>
    {
        ["--bg-primary"]   = "#272822",
        ["--bg-secondary"] = "#3e3d32",
        ["--accent"]       = "#f92672",
        // ... all required variables (see section 4)
    }
);
```

**ThemeDefinition fields:**

| Field | Type | Notes |
|-------|------|-------|
| `Key` | `string` | Unique within your plugin. Combined with `PluginId` to form the storage key `"pluginId:key"` saved in user preferences. Use lowercase kebab-case. |
| `Label` | `string` | Short human-readable name, e.g. `"Monokai"`. |
| `Description` | `string` | One-line description shown below the label in the theme picker. |
| `Swatches` | `string[]` | **Exactly three** hex colour strings: `[background, accent, midtone]`. Rendered as preview dots. Chronicle will pad with `#808080` or truncate if you don't provide exactly three, so always supply the correct count. |
| `Variables` | `IReadOnlyDictionary<string, string>` | Complete set of CSS custom-property overrides. See [section 4](#4-the-css-variable-contract) for the full list. |

---

### 4. The CSS variable contract

Chronicle applies your theme by setting each entry from `Variables` as an inline style on `document.documentElement`. The `:root` block in the base stylesheet defines light-theme fallback values. **Your theme does not need to re-declare structural constants** (`--sidebar-width`, `--header-height`, `--radius`) — those are not part of the theming contract.

You **must** provide all of the following variables. Missing entries fall back to the light-theme `:root` values, which will look wrong on a dark theme.

#### Backgrounds

| Variable | Purpose |
|----------|---------|
| `--bg-primary` | Page / sidebar background |
| `--bg-secondary` | Panel / content area background |
| `--bg-card` | Card and list-item background |
| `--surface` | Modal and popover base surface |
| `--surface-raised` | Elevated surface (dropdowns, tooltips) |
| `--surface-2` | Subtle surface variation |
| `--hover-bg` | Background tint on hover |

#### Accent / interactive

| Variable | Purpose |
|----------|---------|
| `--accent` | Primary action colour (buttons, links, highlights) |
| `--accent-hover` | Darker/lighter shade for hover state |
| `--accent-subtle` | Low-opacity tint of accent (badge backgrounds, focus rings) |
| `--accent-fg` | Foreground colour when rendered on `--accent` (usually `#fff` or `#000`) |

#### Text

| Variable | Purpose |
|----------|---------|
| `--text-primary` | Body text |
| `--text-secondary` | De-emphasised text (labels, metadata) |
| `--text-muted` | Placeholder text, disabled states |

#### Borders & shadow

| Variable | Purpose |
|----------|---------|
| `--border` | Default border colour |
| `--shadow` | Default box-shadow (full value, e.g. `0 2px 8px rgba(0,0,0,0.5)`) |

#### Semantic colours

| Variable | Purpose |
|----------|---------|
| `--success` | Success text / icon colour |
| `--warning` | Warning text / icon colour |
| `--danger` | Danger / error text / icon colour |

#### Status badge tokens

These control the coloured pill badges used throughout the UI (library status, enrichment state, etc.).

| Variable | Purpose |
|----------|---------|
| `--status-success-bg` | Background |
| `--status-success-fg` | Foreground / text |
| `--status-success-border` | Border |
| `--status-danger-bg` | Background |
| `--status-danger-fg` | Foreground / text |
| `--status-danger-border` | Border |
| `--status-warning-bg` | Background |
| `--status-warning-fg` | Foreground / text |
| `--status-warning-border` | Border |
| `--status-neutral-bg` | Background |
| `--status-neutral-fg` | Foreground / text |
| `--status-neutral-border` | Border |

**Tip for dark themes:** Use semi-transparent colour values (`rgba(...)`) for badge backgrounds and borders rather than flat hex colours — they sit more naturally on varied card backgrounds.

---

### 5. Write the manifest

Create `manifest.json` in your project root alongside the `.csproj`. The build rule you added copies it to the output directory automatically.

```json
{
  "plugin_id": "chronicle.plugin.themes.yourname",
  "name": "Your Name Themes",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "A short description of your theme pack.",
  "iconUrl": null,
  "supportedInterfaces": ["IThemePlugin"],
  "settings": []
}
```

**Rules:**
- `plugin_id` must exactly match the `PluginId` property you return from your C# class.
- `supportedInterfaces` must include `"IThemePlugin"`.
- `settings` must be an empty array — theme plugins do not support user-configurable settings.
- `iconUrl` can be `null` or a URL to a square PNG/SVG icon (shown in the Installed Plugins list).

---

### 6. Build and deploy

```bash
# Build in Release mode
dotnet build -c Release

# The output is in:
#   bin/Release/net9.0/Chronicle.Plugin.Themes.YourName.dll
#   bin/Release/net9.0/manifest.json
```

Copy both files to a subfolder inside Chronicle's plugins directory:

```
Chronicle/
└── src/
    └── Chronicle.API/
        └── plugins/
            └── chronicle.plugin.themes.yourname/   ← folder name = plugin_id
                ├── Chronicle.Plugin.Themes.YourName.dll
                └── manifest.json
```

Restart the Chronicle API. Your themes will appear immediately in **Settings → Plugins → Themes** and **Settings → Preferences → Theme**.

> **Do not copy `Chronicle.Plugins.dll`** into your plugin folder. Chronicle provides it from the host process. Copying it causes assembly identity conflicts and will prevent your plugin from loading.

---

## Tips and conventions

- **One class per file.** Keep each `ThemeDefinition` as a `private static readonly` field — not a separate class.
- **Static initialisation order matters.** Declare your `AllThemes` list *after* all the individual theme fields in the source file. C# initialises static fields in textual order, so referencing a field before it is declared results in a null reference.
- **Theme keys are user-facing identifiers.** Once you publish a theme, avoid changing its `Key` — it is saved in user preferences as `"yourpluginid:yourthemekey"`. Changing it orphans any user who had it selected.
- **Your plugin can return any number of themes.** One is fine; twenty is fine. They all appear in the same picker alongside themes from other plugins.
- **No Chronicle.Plugins.dll in output.** The `Private="false" ExcludeAssets="runtime"` attributes on the project reference ensure this. If you add any other dependency that transitively brings in Chronicle.Plugins, audit your output directory before deploying.
- **Plugin ID namespace.** Use the reverse-domain convention: `chronicle.plugin.themes.<yourname>`. This avoids collisions with other community plugins.

---

## Reference: all CSS variables

A complete minimal theme with every required variable:

```csharp
private static readonly ThemeDefinition MyTheme = new(
    Key:         "mytheme",
    Label:       "My Theme",
    Description: "A short description.",
    Swatches:    ["#bg-hex", "#accent-hex", "#midtone-hex"],
    Variables: new Dictionary<string, string>
    {
        // Backgrounds
        ["--bg-primary"]           = "",
        ["--bg-secondary"]         = "",
        ["--bg-card"]              = "",
        ["--surface"]              = "",
        ["--surface-raised"]       = "",
        ["--surface-2"]            = "",
        ["--hover-bg"]             = "",

        // Accent
        ["--accent"]               = "",
        ["--accent-hover"]         = "",
        ["--accent-subtle"]        = "",
        ["--accent-fg"]            = "",

        // Text
        ["--text-primary"]         = "",
        ["--text-secondary"]       = "",
        ["--text-muted"]           = "",

        // Borders & shadow
        ["--border"]               = "",
        ["--shadow"]               = "",

        // Semantic
        ["--success"]              = "",
        ["--warning"]              = "",
        ["--danger"]               = "",

        // Status badge tokens
        ["--status-success-bg"]    = "",
        ["--status-success-fg"]    = "",
        ["--status-success-border"]= "",
        ["--status-danger-bg"]     = "",
        ["--status-danger-fg"]     = "",
        ["--status-danger-border"] = "",
        ["--status-warning-bg"]    = "",
        ["--status-warning-fg"]    = "",
        ["--status-warning-border"]= "",
        ["--status-neutral-bg"]    = "",
        ["--status-neutral-fg"]    = "",
        ["--status-neutral-border"]= "",
    }
);
```

Copy this template, fill in every value, and you have a complete, valid theme. Use the themes in this repository as worked examples.
