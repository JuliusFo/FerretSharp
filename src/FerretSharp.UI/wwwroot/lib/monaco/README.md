# Monaco Editor (vendored)

- Package: `monaco-editor` **0.57.0** (MIT, see `LICENSE.txt` and `ThirdPartyNotices.txt`)
- Source: `https://registry.npmjs.org/monaco-editor/-/monaco-editor-0.57.0.tgz`
- Integrity (npm, of the tarball): `sha512-5BkI9KGoqrNvBGUe15/QlZq3OooZ8WLg1AxTpaqHRCP3HNpzPPZKE2EDz8M7c+VRmCeUw1Brp4cx/PWm3kI/5A==`
  (shasum `9889fb51ddc27fe90e6da0e96c237c852adc4654`)
- Content: the AMD build `package/min/vs` (ADR 0011), loaded through `vs/loader.js` by `js/monaco.js` – no bundler.
- Trimmed: the language services of TypeScript/JavaScript, CSS, HTML and JSON (`vs/language/`, their workers in
  `vs/assets/`, about 17 MB) and all UI translations except German (`vs/nls/lang/de.js`). The syntax colouring of all
  languages (`*-<hash>.js`, loaded on demand) and the editor worker stay.

The AMD build is deprecated upstream and may disappear in a later version; this one stays usable as it is. Upgrading:
extract `min/vs` of the new tarball, trim the same way, update version and integrity here and in `CLAUDE.md`.
