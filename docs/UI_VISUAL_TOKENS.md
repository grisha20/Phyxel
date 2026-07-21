# Phyxel UI visual tokens

The runtime UI remains self-contained and reproducible. KNI sprite fonts use the
Windows `Segoe UI` fallback already declared in `Content/Fonts/*.spritefont`; the
build does not depend on a separately installed Inter font or an untracked font
file.

The editable Figma specification uses Inter as the closest design-time analogue.
It records `Segoe UI` as the runtime fallback. No Inter binary is redistributed by
this repository, so no additional font licence is required by the build.

## Geometry at 1920x1080, 100% DPI

| Token | Value |
|---|---:|
| top bar | 56 px |
| status bar | 32 px |
| left toolbar | 188 px |
| right properties panel | 316 px |
| material palette | 168 px |
| primary gap | 10 px |
| tool row | 48 px |
| small / medium / large radius | 5 / 8 / 10 px |
| selected stroke | 3 px |

The calculator scales these metrics from resolution and per-monitor DPI, with a
readability floor for 1280x720 and a 1.5x ceiling for high-DPI 2560x1440.

## Semantic colors

| Role | Value | Runtime source |
|---|---|---|
| window | `#070B0F` | `UiTheme.WindowBackground` |
| top bar / deep surface | `#0D131A` | `UiTheme.TopBarBackground` |
| panel | `#111820` | `UiTheme.PanelBackground` |
| raised surface | `#18202A` | `UiTheme.PanelHeader` |
| card | `#1D2733` | `UiTheme.CardBackground` |
| hover | `#25313E` | `UiTheme.CardHover` |
| pressed | `#2B3947` | `UiTheme.CardPressed` |
| border | `#2B3947` | `UiTheme.BorderColor` |
| strong border | `#435365` | `UiTheme.BorderHighlight` |
| primary text | `#EDF2F7` | `UiTheme.TextPrimary` |
| secondary text | `#9DA8B7` | `UiTheme.TextSecondary` |
| muted text | `#697687` | `UiTheme.TextMuted` |
| accent | `#F2B655` | `UiTheme.PrimaryAccent` |
| accent pressed | `#D89A35` | `UiTheme.AccentPressed` |
| action blue | `#46A4E1` | `UiTheme.SliderAccent` |
| success | `#48C78E` | `UiTheme.StatusGreen` |
| danger | `#EE5252` | `UiTheme.Danger` |

Category colors remain semantic: powders `#DAB85C`, liquids `#4BA0EB`, gases
`#8CC8BE`, solids `#A591D7`, combustion `#F06432`, tools `#AFB9C3`.

## States

Buttons, tool rows, category tabs and material cards share the state order
`idle -> hover -> pressed -> active/selected -> disabled`. Disabled controls do
not emit actions. The selected material uses a 3 px accent border; status text
stays neutral and only its indicator dot uses success or pause color.
