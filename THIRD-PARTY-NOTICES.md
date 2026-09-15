# Third-party notices

pdfview is MIT licensed (see `LICENSE`). It redistributes the following, each
under its own terms.

## pdf.js

Everything under `vendor/` is [pdf.js](https://github.com/mozilla/pdf.js)
4.10.38, which renders the pages.

    Copyright 2024 Mozilla Foundation

Licensed under the Apache License, Version 2.0. The full licence text is in
`vendor/LICENSE-APACHE-2.0.txt`, and the notice also rides along in the header
of `vendor/pdf.min.mjs` and `vendor/pdf.worker.min.mjs`.

pdf.js ships two sets of support files that carry their own licences:

- `vendor/cmaps/` - character maps for CJK text. See `vendor/cmaps/LICENSE`.
- `vendor/standard_fonts/` - replacements for the PDF standard fonts. See
  `vendor/standard_fonts/LICENSE_FOXIT` and
  `vendor/standard_fonts/LICENSE_LIBERATION`.

## Microsoft.Web.WebView2

The app hosts the page in WebView2. The NuGet package is restored at build time
and its runtime ships with Windows; neither is redistributed in this repository.
It is covered by the Microsoft Software License Terms that come with the
package.

## Windows.Data.Pdf

The Explorer thumbnail handler rasterises the first page with `Windows.Data.Pdf`,
which is part of Windows. Nothing is redistributed.
