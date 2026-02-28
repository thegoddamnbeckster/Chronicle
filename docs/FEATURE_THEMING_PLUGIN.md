# Feature Design: Font & Colour Theming Plugin

**Status:** Design/Planning
**Target:** Phase 3
**Goal:** Allow users to customise Chronicle's appearance (fonts, colours, spacing) through a dedicated theming system. Themes can be bundled as plugins, selected by users, and override the default Sonarr/Radarr-inspired aesthetic.

---

## Overview

The theming system works by:
1. Exposing a new `IThemePlugin` interface in `Chronicle.Plugins`
2. Loading the active theme plugin at runtime (same discovery mechanism as other plugins)
3. Serving the theme as CSS custom properties (CSS variables) via the Chronicle API
4. The React frontend applies those variables on `<html>` — no page reload required

Users can also define **custom themes** directly in the UI without writing a plugin.

---

## Architecture

### New interface: `IThemePlugin`

```csharp
// Chronicle.Plugins/IThemePlugin.cs

public interface IThemePlugin
{
    string PluginId    { get; }
    string Name        { get; }
    string Version     { get; }
    string Author      { get; }
    string Description { get; }

    /// <summary>
    /// Returns the complete set of CSS variable overrides for this theme.
    /// Keys are CSS variable names WITHOUT the leading "--" (e.g. "color-bg-primary").
    /// Values are valid CSS values (e.g. "#1a1a2e", "Inter, sans-serif", "0.875rem").
    /// </summary>
    IReadOnlyDictionary<string, string> GetThemeVariables();

    /// <summary>
    /// Optional: returns raw CSS to inject after the variable block.
    /// Use sparingly — prefer variables where possible.
    /// </summary>
    string? GetAdditionalCss() => null;
}
```

### CSS Variable Contract

All Chronicle UI components consume a canonical set of CSS custom properties. Theme plugins override these variables to restyle the entire application.

#### Colour variables

| Variable | Default (dark) | Description |
|---|---|---|
| `--color-bg-primary` | `#1f2023` | Main page background |
| `--color-bg-surface` | `#2b2d30` | Card / panel background |
| `--color-bg-surface-alt` | `#35373b` | Alternate surface (e.g. table rows) |
| `--color-bg-input` | `#1a1c1e` | Input field background |
| `--color-border` | `#3e4045` | Default border colour |
| `--color-border-focus` | `#5865f2` | Focused element border |
| `--color-accent` | `#5865f2` | Primary accent (buttons, links, highlights) |
| `--color-accent-hover` | `#4752c4` | Accent hover state |
| `--color-accent-danger` | `#ed4245` | Destructive actions |
| `--color-accent-success` | `#57f287` | Success states |
| `--color-accent-warning` | `#faa61a` | Warning states |
| `--color-text-primary` | `#e3e5e8` | Main text |
| `--color-text-muted` | `#96989d` | Secondary / placeholder text |
| `--color-text-inverse` | `#ffffff` | Text on accent backgrounds |
| `--color-overlay` | `rgba(0,0,0,0.6)` | Modal backdrop |

#### Typography variables

| Variable | Default | Description |
|---|---|---|
| `--font-sans` | `'Inter', 'Segoe UI', system-ui, sans-serif` | UI font stack |
| `--font-mono` | `'JetBrains Mono', 'Fira Code', monospace` | Code / metadata font |
| `--font-size-xs` | `0.75rem` | Extra small text |
| `--font-size-sm` | `0.875rem` | Small text |
| `--font-size-base` | `1rem` | Body text |
| `--font-size-lg` | `1.125rem` | Large text |
| `--font-size-xl` | `1.25rem` | Section headings |
| `--font-size-2xl` | `1.5rem` | Page headings |
| `--font-weight-normal` | `400` | Regular weight |
| `--font-weight-medium` | `500` | Medium weight |
| `--font-weight-bold` | `700` | Bold weight |
| `--line-height-tight` | `1.25` | Compact line height |
| `--line-height-base` | `1.5` | Normal line height |

#### Spacing & shape variables

| Variable | Default | Description |
|---|---|---|
| `--radius-sm` | `4px` | Small corner radius |
| `--radius-md` | `6px` | Default corner radius |
| `--radius-lg` | `8px` | Large corner radius |
| `--radius-xl` | `12px` | Extra large (cards) |
| `--shadow-sm` | `0 1px 2px rgba(0,0,0,0.3)` | Subtle shadow |
| `--shadow-md` | `0 4px 12px rgba(0,0,0,0.4)` | Card shadow |
| `--transition-fast` | `120ms ease` | Quick UI transitions |
| `--transition-base` | `200ms ease` | Default transitions |

---

## API

### Theme endpoint

```
GET  /api/v1/themes              List available themes (built-in + plugins)
GET  /api/v1/themes/active       Get the currently active theme's CSS variables
PUT  /api/v1/themes/active       Set active theme { themeId: string }
GET  /api/v1/themes/custom       Get user's custom variable overrides
PUT  /api/v1/themes/custom       Save custom variable overrides
GET  /api/v1/themes/{id}/css     Get the complete compiled CSS for a theme
```

The frontend calls `GET /api/v1/themes/active` on startup and applies the returned variables to `:root { --color-bg-primary: ...; }` dynamically.

---

## Built-in Themes

| Theme ID | Description |
|---|---|
| `default-dark` | Default dark theme (Sonarr/Radarr aesthetic) |
| `default-light` | Light mode variant |
| `oled-dark` | Pure black OLED-optimised dark theme |
| `high-contrast` | Accessibility-focused high contrast theme |

Built-in themes are defined as static `Dictionary<string, string>` in `Chronicle.Services/Theming/BuiltInThemes.cs` — no plugin required.

---

## User Custom Themes

Users can override any subset of variables through the UI:

1. Settings → Appearance → Custom Theme
2. Colour pickers and font dropdowns for each variable category
3. Live preview of changes before saving
4. Saved to `user_preferences.theme_overrides` as JSON (merged on top of the active theme at runtime)
5. Export/import as a `.json` file

---

## Plugin Themes

A theme plugin is identical to any other Chronicle plugin — a DLL implementing `IThemePlugin`. Example:

```csharp
// Chronicle.Plugin.Theme.Catppuccin
public class CatppuccinMochaTheme : IThemePlugin
{
    public string PluginId    => "theme-catppuccin-mocha";
    public string Name        => "Catppuccin Mocha";
    public string Version     => "1.0.0";
    public string Author      => "Example Author";
    public string Description => "Catppuccin Mocha — soothing pastel theme";

    public IReadOnlyDictionary<string, string> GetThemeVariables() =>
        new Dictionary<string, string>
        {
            ["color-bg-primary"]   = "#1e1e2e",
            ["color-bg-surface"]   = "#181825",
            ["color-accent"]       = "#cba6f7",
            ["color-text-primary"] = "#cdd6f4",
            ["font-sans"]          = "'Inter', sans-serif",
            // ... etc
        };
}
```

---

## Frontend Integration

In `src/Chronicle.Web/src/`:

- `styles/variables.css` — declares all `--*` variables with defaults
- `hooks/useTheme.ts` — fetches active theme from API, applies to `:root`
- `components/settings/ThemeSettings.tsx` — theme picker + custom colour editor

```typescript
// hooks/useTheme.ts
export function useTheme() {
  useEffect(() => {
    fetch('/api/v1/themes/active')
      .then(r => r.json())
      .then(({ data }) => {
        const root = document.documentElement
        Object.entries(data.variables).forEach(([key, value]) => {
          root.style.setProperty(`--${key}`, value as string)
        })
      })
  }, [])
}
```

Called once in `App.tsx` — variables propagate instantly to all components via CSS inheritance.

---

## Google Fonts Integration

Themes can specify a Google Font family. The frontend detects this and injects a `<link>` to Google Fonts:

```json
{
  "font-sans": "'Outfit', sans-serif",
  "_google_fonts": ["Outfit:wght@400;500;700"]
}
```

The `_google_fonts` key is a non-standard meta-field stripped before applying CSS variables.

---

## Implementation Order

1. Add `IThemePlugin` to `Chronicle.Plugins`
2. Extend `IPluginRegistry` to discover and expose `IThemePlugin` instances
3. Add `ThemingService` + `IThemingService` to `Chronicle.Services`
4. Add `ThemeController` REST endpoints
5. Add built-in themes (`BuiltInThemes.cs`)
6. Store active theme ID + custom overrides in `app_settings` / `user_preferences`
7. Frontend: `useTheme` hook, ThemeSettings page, colour picker component

---

## Security Note

Theme plugins that supply `GetAdditionalCss()` must be sandboxed:
- The raw CSS string is served via `GET /api/v1/themes/{id}/css` and injected via a `<style>` tag
- Chronicle sanitises the CSS using a CSP-safe allowlist (no `url()` with external origins, no `expression()`, no `@import`)
- Plugin trust is managed through the same plugin signing mechanism described in `FEATURE_PLUGIN_SECURITY.md`
