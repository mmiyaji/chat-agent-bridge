# Third-party licenses

The About page reads `ChatAgentBridge.Desktop/Assets/Licenses/catalog.json` and the
referenced documents without network access. The project copies that directory
and `THIRD-PARTY-NOTICES.txt` into both build and publish output. New portable
archives include them in the updater's managed file manifest and payload checksums.

## Reviewed distribution

The inventory records the restored package versions, package hashes, source URLs,
notice filenames, and the self-contained runtime. It covers the current desktop
and updater dependencies, including native Windows App SDK components, WebView2
loader/projection, Windows SDK/C#/WinRT, Community Toolkit, .NET, and
the Fluent System Icons SVG/font subset. It includes vendor notices as supplied;
some describe optional components beyond the features used by this app.

Build tools are recorded separately from redistributed runtime components.
Windows-provided APIs/fonts and externally installed agent CLIs are not copied
into this app. The older Node SEA distribution has its own notice generation in
`scripts/build-exe.mjs`; its runtime is not part of this Windows portable payload.

The individual texts keep their upstream copyright and license terms. These
notices do not assign a new license to Chat Agent Bridge's own source code.

## Updating dependencies or assets

1. Review the actual desktop publish `.deps.json`, its runtime configuration,
   updater publish/build dependencies, and any newly copied native files or assets.
   Inspect transitive packages as well as direct references. Runtime packs and
   generated projection references may not have `type: package` in `.deps.json`.
2. Retrieve license/copyright/NOTICE texts from the exact restored package or its
   pinned primary repository revision. Preserve the full applicable text; do not
   classify every Microsoft package as MIT. Record source URLs and package versions
   in `Assets/Licenses/inventory.json`.
3. Update the individual notice documents, catalog, and aggregate
   `THIRD-PARTY-NOTICES.txt`. Update the SHA-256/byte counts in the inventory.
   `.gitattributes` preserves notice bytes so checkout line endings cannot alter
   the recorded hashes. Keep the catalog in sync with `noticeDocuments`.
4. Publish, then run:

   ```powershell
   ./desktop/Test-ThirdPartyNotices.ps1 -SelfTest
   ./desktop/Test-ThirdPartyNotices.ps1 -PublishDirectory <publish-directory>
   ```

   CI and new archive creation run this validation. It checks catalog coverage,
   bundled notice/inventory integrity, published desktop package/runtime-pack/
   projection identities, and the self-contained .NET version. A newer SDK may
   choose a newer runtime patch; review/update its inventory rather than bypassing
   a version failure. Updater-specific dependency changes also require the manual
   review in step 1; the desktop `.deps.json` cannot expose dependencies embedded
   only in the updater's single-file executable.
5. Open About > Third-party licenses and verify new entries, readable wrapping,
   document-end scrolling, and closing in both languages/themes.

Validation of historical release ZIPs remains compatible with releases made before
these notices were added. The new notice check applies when creating new archives.
