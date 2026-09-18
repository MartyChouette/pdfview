# Changelog

## Unreleased

- Explorer thumbnails no longer draw the pdfview mark into the corner. The
  shell already composites the file association icon over the preview, so the
  icon was showing up twice.
- Faster launch. Median warm start to the first page on screen went from 988 ms
  to 915 ms over 36 interleaved launches, from compiling ahead of time, opening
  the window before the plumbing behind it, cutting Edge start-up work the
  viewer has no use for, fetching pdf.js alongside the viewer instead of after
  it, and reading the state file once per process instead of three times per
  window.
- The recent list no longer stats files on network paths or disconnected
  drives. One entry on an unreachable share used to stall opening a document.
- `PDFVIEW_TRACE=1` writes a startup timeline to
  `%LOCALAPPDATA%\pdfview\trace.log`.

## 1.0.0

First public release.

- Windows desktop app: its own window, icon and taskbar entry, registerable as
  the default PDF reader.
- Continuous scrolling with pages rendered on demand and released when they
  scroll away, so long documents cost about what short ones do.
- Selectable text, working links, outline and thumbnail sidebar.
- Whole-document search reporting `n of m`, every match on the page highlighted.
- Three page layouts: single, two pages, and two pages with the cover alone.
  Page Down moves a spread at a time.
- Each file reopens on the page you left it on.
- Light and dark themes, plus inverted pages for reading white-on-black.
- Page previews for PDF icons in Explorer, drawn by the renderer built into
  Windows and badged in the corner.
- Printing through the standard Windows printer dialog, sending the document's
  own vectors and text with no header or footer added.
