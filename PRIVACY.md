# Privacy statement / Gizlilik

**This program will not transfer any information to other networked systems unless specifically requested by the
user or the person installing or operating it.**

**Bu program, kullanıcı ya da onu kuran/çalıştıran kişi açıkça istemedikçe hiçbir bilgiyi başka ağ sistemlerine
aktarmaz.**

NitroDrop has no telemetry, no analytics, no crash reporting, no advertising, no accounts and no automatic update
check. It does not contact Nitrobyte's servers. Settings, history, logs and keys stay on your PC in
`%LOCALAPPDATA%\airdrop-win`.

## What goes over the network, and when

Everything below happens only because you use the corresponding feature, and only with devices or sites named here.

| Feature | Who it talks to | What is sent |
|---|---|---|
| AirDrop (receive / send) | Devices **near you**: on your local network (mDNS on UDP 5353 + HTTPS on the AirDrop port) and, with the Wi-Fi driver, directly over AWDL; Bluetooth LE advertisements for discovery | Your device name (Ayarlar → Görünen ad), a model name, the files/links **you** choose to send, and the usual AirDrop handshake data. Received files are saved only after you accept them (or per your "Kimden alınsın" setting). |
| PC ↔ PC sync (tabs, apps, clipboard) | Only NitroDrop PCs **you paired** with a 6-digit code, on your local network | Open tab titles/URLs, app list, clipboard items you share; TLS with pinned keys + HMAC. |
| Browser extension | Only the local NitroDrop app on the same PC (native messaging) | Tab information, to the app; nothing leaves the PC unless sync is on. |
| iPhone Shortcut "PC'de Aç" | Your iPhone, on your local network, to a receiver on your PC | The link/app name you send from the Shortcut, authenticated with a per-device token. |
| iPhone continuity (Bluetooth) | Your **paired** iPhone over Bluetooth | Notifications, messages and call control between your phone and your PC; nothing is uploaded anywhere. |
| Direct iPhone link — Wintun download | `www.wintun.net` (WireGuard LLC), **once**, after you click "İndir" in the prompt "Wi-Fi bileşeni indirilecek (Wintun, WireGuard LLC)" | A normal HTTPS download request for `wintun-0.14.1.zip`; the file is verified (SHA-256 + WireGuard LLC signature) and stored locally. |
| Advanced Setup wizard — driver firmware | `git.kernel.org` (fallback `gitlab.com`, linux-firmware), only when you install the Wi-Fi driver | A normal HTTPS download request for the Intel firmware and its licence (pinned SHA-256). |
| Links you click | Your browser opens nitrobyte.com.tr, the GitHub repository or Microsoft's BitLocker recovery page | Only when you click them. |

Downloads send what any HTTPS download sends (your IP address, a user agent); NitroDrop adds no identifiers.

## Contacts

The optional contact book (vCard files you import) is used locally to show names/photos of senders. For AirDrop's
"Contacts only" mode, hashes of identifiers are compared as the AirDrop protocol requires; the contact book itself is
never uploaded.

## Questions

Open an issue on the repository or contact Nitrobyte (https://nitrobyte.com.tr).
