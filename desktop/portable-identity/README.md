# Optional portable notification identity

The portable identity package is an optional compatibility path for Windows toast activation. Ordinary portable builds keep the existing notification behavior and do not require a package identity.

## Build

The desktop project uses `desktop/portable-identity/app.portable.manifest` only when published with both `-p:CABPortable=true` and `-p:CABPortableIdentity=true`. A normal portable build with only `CABPortable=true` keeps its legacy application manifest.

To create a signed identity bundle during archive creation, provide a code-signing certificate thumbprint from the current user's `Cert:\CurrentUser\My` store:

```powershell
.\desktop\Create-PortableArchive.ps1 -IdentityCertificateThumbprint '<thumbprint>'
```

The identity bundle uses package name `mmiyaji.ChatAgentBridge`, publisher `CN=mmiyaji`, application ID `App`, and fixed package version `1.0.0.0`, independent of the app's release version. Keep these identity values stable across updates. A future identity version requires an explicit migration; unsupported versions must be rejected. `-IdentityCertificateThumbprint` signs and includes the identity bundle. `-ExistingLayout` is for a layout that was already published with the identity fusion manifest and contains the matching EXE; it does not convert a legacy executable. For a fresh build, omitting the thumbprint keeps the ordinary legacy path. An existing layout must already contain a valid identity bundle to preserve one.

Earlier local/pre-release builds used the package name `ChatAgentBridge.Portable`. The app does not silently migrate or remove that registration. Setup refuses to register the new identity while a matching previous registration (`CN=mmiyaji`) exists; remove that previous registration explicitly after verifying its location and closing its app process, then retry setup. Its certificate trust is left untouched.

The lower-level builder also accepts an optional timestamp URL. It makes no timestamp request unless `-TimestampUrl` is supplied:

```powershell
.\desktop\New-PortableIdentityPackage.ps1 -Layout '<identity-published-layout>' -CertificateThumbprint '<thumbprint>' -TimestampUrl '<approved-HTTP-or-HTTPS-timestamp-service>'
```

Use the timestamp service approved for your signing certificate and release process. Production distribution requires a CA-trusted code-signing certificate or an IT-managed trust arrangement. This development setup has no production signing certificate: the local self-signed test bundle is not suitable for production distribution. A developer registration test is only a test workflow; Windows Developer Mode is not a distribution requirement.

The app asks for explicit user consent before trusting a certificate, and trust is scoped to that Windows user. If setup fails after adding a leaf certificate, it rolls that addition back. An already trusted CA-signed bundle can work without administrator rights; Windows behavior for self-signed certificates varies, so do not rely on the local test certificate as a universal no-admin setup.

The ZIP may contain the public `.cer` leaf certificate and signed `.msix` identity package. Never place a PFX, private key, or other signing secret in the portable payload.

Registration keeps the stable identity across app updates and retains the existing portable data and per-user startup configuration. Identity setup remains optional; existing notifications continue to work without it.
