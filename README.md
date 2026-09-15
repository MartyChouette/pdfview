# pdfview

<img src="docs/icon.png" width="72" align="right" alt="">

A small, quiet PDF reader for Windows. Its own window, its own icon, and it can
be the program Windows opens PDFs with.

![pdfview showing a two-page spread](docs/screenshot.png)

## What it does

- Continuous scrolling, pages drawn as they come into view
- Selectable text, working links and outline
- One page, two pages, or two pages with the cover on its own
- Search across the whole document
- Reopens every file on the page you left it on
- Light, dark, and inverted pages for night reading
- PDF icons in Explorer show the first page

It does not edit, annotate, sign or fill in forms. It reads.

## Install

Download from [releases](https://github.com/MartyChouette/pdfview/releases),
unzip somewhere you will keep it, run **install.cmd**.

Then make it the default: **Settings > Apps > Default apps**, search for
pdfview, set it for `.pdf`. Windows does not let a program do that step itself.

- Needs Windows 10 1809 or newer and the Edge WebView2 runtime, which is already
  on Windows 11.
- Unsigned, so Windows flags it. Right-click the zip, **Properties**,
  **Unblock**, then extract.
- Keep the folder where you put it. The registration points at that path.

`install.cmd` writes only to `HKEY_CURRENT_USER` and needs no admin.
`uninstall.cmd` undoes it.

<details>
<summary><b>Shortcuts</b></summary>

| Key | Action |
| --- | --- |
| `Ctrl+O` | Open a file |
| `Ctrl+F` | Find in document |
| `Enter` / `Shift+Enter` | Next / previous match |
| `Page Down` / `Page Up` | Next / previous page or spread |
| `Home` / `End` | First / last page |
| `Ctrl+G` | Go to page |
| `Ctrl+P` | Print |
| `+` `-` `0` | Zoom in, out, fit width |
| `Ctrl+scroll` | Zoom |
| `D` | Single page / two pages / two pages with cover |
| `R` | Rotate 90 degrees |
| `S` | Sidebar (thumbnails, outline) |
| `T` | Light / dark theme |
| `I` | Invert page colours |
| `F` | Fullscreen |
| `Esc` | Close the find bar, or leave fullscreen |

</details>

## Build

Needs the [.NET SDK 8 or newer](https://dotnet.microsoft.com/download).

```
build.cmd
install.cmd
```

## How it works

A WinForms window hosting WebView2, with
[pdf.js](https://github.com/mozilla/pdf.js) rendering inside it. Nothing listens
on a network port; the viewer's files and the PDF stream are answered inside the
process.

Explorer thumbnails come from a shell handler in `src/PdfThumb`, drawn by
`Windows.Data.Pdf` and run in the isolated host Windows keeps for the purpose.

## Licence

MIT, see [LICENSE](LICENSE). Rendering is
[pdf.js](https://github.com/mozilla/pdf.js) by the Mozilla Foundation under
Apache 2.0, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
