# NeoIDE Marketplace

Themes (and soon plugins) for [NeoIDE](https://github.com/NeoPHP-Framework/NeoIDE), the IDE of the NeoPHP framework.

NeoIDE reads this repository directly: **Settings › Themes › Marketplace** lists the themes of `index.json`,
downloads the JSON file of the theme you choose and installs it in `%APPDATA%\NeoIDE\themes\`.
When the `version` of a theme increases here, NeoIDE offers the update.

## Content

| Path | |
|---|---|
| `index.json` | Catalogue read by NeoIDE — **generated**, do not edit by hand |
| `themes/<id>.json` | One file per theme |
| `schemas/theme.schema.json` | JSON schema of a theme (validation + completion in your editor) |
| `tools/build-index.ps1` | Validates the themes and regenerates `index.json` |
| `plugins/` | Reserved for the plugins |

## Themes

| Theme | Type |
|---|---|
| Neo Dark | dark — default, shipped with NeoIDE |
| Neo Light | light — shipped with NeoIDE |
| Darcula | dark |
| Dracula | dark |
| One Dark | dark |
| Monokai | dark |
| Nord | dark |
| GitHub Light | light |
| Solarized Light | light |

## Add or change a theme

1. Create `themes/<id>.json` (copy an existing theme to start):

   ```json
   {
     "$schema": "https://raw.githubusercontent.com/NeoPHP-Framework/neo-ide-marketplace/main/schemas/theme.schema.json",
     "id": "my-theme",
     "name": "My Theme",
     "version": "1.0.0",
     "author": "You",
     "description": "One sentence shown in NeoIDE.",
     "dark": true,
     "ui": { "Accent": "#FF8800", "PanelBg": "#22252B" },
     "editor": { "background": "#1B1D22", "selection": "#2F4A70" },
     "syntax": {
       "Keyword": "#FF8800",
       "Comment": { "color": "#6B7280", "italic": true }
     }
   }
   ```

   Only `id`, `name`, `version`, `author`, `description` and `dark` are required: every colour you leave out
   comes from **Neo Dark** (`"dark": true`) or **Neo Light** (`"dark": false`).
   Colours are `#RRGGBB` or `#RRGGBBAA`.

2. Regenerate the catalogue:

   ```powershell
   pwsh tools/build-index.ps1
   ```

3. Commit `themes/<id>.json` **and** `index.json`, open a pull request. The *Validate* workflow checks the files.

To publish a new version of a theme, change its colours **and increase `version`** (1.0.0 → 1.0.1).

You can also test a theme without publishing it: **Settings › Themes › Import .json…** in NeoIDE.

## Keys

- `ui` — interface: `WindowBg`, `TitleBarBg`, `PanelBg`, `EditorBg`, `SeparatorBrush`, `SubtleBorder`, `HoverBg`, `PressedBg`,
  `SelectedBg`, `SelectedInactiveBg`, `Fg`, `FgSecondary`, `FgDisabled`, `Accent`, `AccentHover`, `AccentFg`, `InputBg`,
  `InputBorder`, `InputFocusBorder`, `ButtonBg`, `ButtonBorder`, `PopupBg`, `PopupBorder`, `TabActiveBg`, `TabBarBg`,
  `ScrollThumb`, `ScrollThumbHover`, `ErrorFg`, `SuccessFg`, `WarnFg`, `LinkFg`, `ConsoleBg`, `WelcomeSideBg`, `WelcomeMainBg`.
- `editor` — `background`, `currentLine`, `selection`, `lineNumbers`, `activeLineNumber`, `unused`, `errorUnderline`,
  `bracketMatch`, `searchHighlight`, `caret`.
- `syntax` — `Default`, `Keyword`, `String`, `StringEscape`, `Number`, `Comment`, `DocComment`, `DocTag`, `Variable`, `Property`,
  `Constant`, `Function`, `FunctionCall`, `StaticCall`, `Type`, `Metadata`, `Tag`, `Attribute`, `AttributeValue`, `Entity`,
  `Delimiter`, `Selector`, `CssProperty`, `CssValue`, `Key`, `JsonKey`, `Heading`, `Bold`, `Italic`, `Link`, `Code`,
  `Operator`, `Punctuation`, `Builtin`, `Inserted`, `Deleted`.
