# Lucide (paths only)

- Package: `lucide-static` **1.55.0** (ISC, parts derived from Feather under MIT, see `LICENSE.txt`)
- Source: `https://unpkg.com/lucide-static@1.55.0/icons/<name>.svg`
- Nothing of Lucide is loaded at run time: the inner elements of each SVG are copied into
  `Components/Common/Icons.cs`, with the Lucide name in the summary of each icon (ADR 0035).

Adding an icon: take it from the same version, copy its elements unchanged into `Icons.cs`. Upgrading: change the
version here and in the summary of `Icons`, replace `LICENSE.txt`.
