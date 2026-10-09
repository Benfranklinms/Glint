# Glint

Spotlight-style search for Windows. Press **Alt+Space**, the bar drops in, and any file on your disk is a few letters away — typos included.

![Glint drop-in (mock-up)](docs/glint-drop-in.gif)

> The images here are rendered mock-ups of the design, not screen recordings. Glint v0.1 has not been tested on real Windows hardware yet.

A Windows port of [fsearch](https://github.com/noahdunnagan/fsearch) by Noah Dunnagan (MIT), rebuilt in C#/WPF with a launcher UI.

## What it does

- **Whole-disk name search.** Every file and folder on every fixed drive, ranked as you type.
- **Forgives typos.** 4-letter words forgive a swapped pair (`mian` → `main`), 5+ letters forgive any one mistake (`raedme` → `README.md`).
- **Searches inside files.** `grep:apply_dir` or `regex:fn\s+\w+_dir`, smart-case, with the match highlighted. Enter opens VS Code at the line when `code` is on your PATH.
- **Drops in like Spotlight.** Centered on the monitor your pointer is on, with a short spring and fade. Acrylic glass on Windows 11 22H2+, a solid dark panel elsewhere.
- **Grab the file.** Drag a result into Explorer, a chat or an upload box. Ctrl+C copies the file itself, Ctrl+Shift+C copies its path.

## Keys

| Key | Does |
| --- | --- |
| Alt+Space | Show / hide (change it in the tray menu) |
| ↑ ↓ | Move through results |
| Enter | Open |
| Ctrl+Enter | Show in folder |
| Ctrl+C / Ctrl+Shift+C | Copy file / copy path |
| Esc | Clear, then hide |

## Query syntax

```
readme in:~/Developer          inside a folder
type:image size:>5mb mtime:<7d  kind, size and age
ext:rs,toml cargo               by extension
'exact  ^prefix  suffix$  !exclude
grep:TODO in:~/Projects         inside files
```

Filters: `ext:` `type:` `kind:` `in:` `path:` `size:` `mtime:` `grep:` `regex:` `limit:`.

## How it works

- **Fast mode (administrator):** reads each NTFS drive's master file table with `FSCTL_ENUM_USN_DATA` — seconds for millions of files — then follows the USN change journal, so new, renamed and deleted files show up within about 0.1 s. Pick *Use fast NTFS index* in the tray to restart elevated; *Open at sign-in* then registers a logon task so there is no UAC prompt each time.
- **Normal mode:** a background folder crawl plus `FileSystemWatcher`. Same results, slower first index.
- Names live in flat arrays with a per-name character mask, so most of the disk is ruled out with one AND before any string work. Scoring favours whole-name and word-start matches, your user folder, and shallow paths, and pushes down `Windows`, `AppData`, `node_modules` and friends.
- Content search reads files fresh from disk in parallel. Unlike fsearch there is **no trigram index yet**, so it scans `in:` when given, the name matches when you typed other words, and your user folder otherwise. It skips binaries, files over 4 MB, and folders like `.git`, `node_modules`, `build`, `vendor`, `bin`, `obj`.

## Differences from fsearch

| | fsearch (macOS) | Glint (Windows) |
| --- | --- | --- |
| Interface | CLI + daemon, Rust crate | Launcher bar + tray |
| First index | `getattrlistbulk` | NTFS MFT (admin) or folder crawl |
| Live updates | FSEvents | USN journal or FileSystemWatcher |
| Content search | Trigram index | Parallel scan (index planned) |
| Speed | ~1 ms names | Tens of ms on millions of names (estimate, untested) |

## Build

```
dotnet publish src/Glint.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

GitHub Actions builds `Glint.exe` on every push and attaches it to `v*` releases.

## License

MIT — see [LICENSE](LICENSE). Original fsearch © Noah Dunnagan.
