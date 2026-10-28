# SignPath Foundation başvurusu — Emre için yapılacaklar listesi

Amaç: NitroDrop'un sürümlerini SignPath Foundation'ın ücretsiz açık kaynak kod imzalama sertifikasıyla imzalatmak
(SmartScreen "bilinmeyen yayımcı" uyarısını azaltır, MSIX sertifika kurmadan yüklenir). Repo tarafı hazır: lisans,
üçüncü taraf listesi, imzalama politikası, gizlilik bildirimi, CI iş akışları, imzasız ayrı sürücü paketi, Wintun'un
pakete konmaması. Aşağıdakileri **senin** yapman gerekiyor (hesap açmak, repo yayımlamak, form doldurmak).

Kaynaklar: şartlar https://signpath.org/terms · başvuru https://signpath.org/apply · belgeler https://docs.signpath.io
(GitHub entegrasyonu: "Trusted build systems → GitHub").

## Dürüst risk değerlendirmesi (önce bunu oku)

- **Proje geçmişi:** SignPath "Released" (imzalanacak biçimde zaten yayımlanmış olmalı) ve "actively maintained" şartı
  koyuyor; pratikte bir süredir herkese açık, sürümleri ve biraz kullanıcısı olan projeleri tercih ediyorlar. Repo
  yeni açılacak, ilk sürüm de yeni olacak → **ilk başvuru reddedilebilir ya da "biraz bekleyin" denebilir.** Önerim:
  repoyu aç, 1–2 imzasız sürüm yayımla (iş akışı imzasız da taslak sürüm üretiyor), README/indirme sayfasını düzgün
  tut, birkaç hafta commit geçmişi biriksin, sonra başvur.
- **"No hacking tools / circumvent security measures" maddesi:** İmzalı uygulamanın içindeki Gelişmiş Kurulum sihirbazı
  Windows test imzalama modunu açıyor, bilgisayara kendi kök sertifikasını kurup sürücüyü onunla imzalıyor ve Secure
  Boot'u kapatmayı tarif ediyor. Her adım uyarılı ve geri alınabilir, sürücü de imzalı paketin dışında; yine de
  inceleyen kişi bunu "güvenlik önlemini aşma" gibi görebilir. Başvuruda bunu **saklamadan, baştan açıkla** (aşağıdaki
  önerilen cevaplarda var). Gerekirse sihirbazı ayrı (imzasız) bir araca taşımak da bir seçenek — onlar isterse yaparız.
- **AirDrop / Apple:** Apple protokolüyle birlikte çalışma (interoperability). Kod Apple'dan alınmadı; marka adı
  "AirDrop uyumlu" diye geçiyor. İnceleyen sorarsa böyle anlat.
- **Windows SDK projeksiyonu:** paketlerdeki `Microsoft.Windows.SDK.NET.dll` Windows SDK lisansıyla geliyor (OSI
  değil). .NET SDK'nın her `net10.0-windows10.0.x` uygulamasına otomatik eklediği platform kütüphanesi olduğu için
  "system library" sayılmalı; THIRD-PARTY-NOTICES.md'de açıkça yazıyor. Sorulursa bunu söyle.

## 1. GitHub reposunu aç (herkese açık)

1. GitHub'da **iki adımlı doğrulamayı (MFA) aç** (şart: tüm ekip üyeleri MFA kullanmalı).
2. Organizasyon/repo: depo adresi: **`https://github.com/z3x0fly/nitrodrop`** geçiyor
   (`SettingsViewModel.RepoUrl`, `CODE_SIGNING_POLICY.md`). Bu adla bir `nitrobyte` organizasyonu açıp `nitrodrop`
   reposunu oluştur. Başka ad seçersen bana söyle, bu iki yeri değiştireyim.
3. `CODE_SIGNING_POLICY.md` içinde kullanıcı adı z3x0fly olarak dolduruldu.
4. Repo boş (README'siz) açılsın. Yerelde:

   ```powershell
   cd "C:\Users\Emre Can Temur\airdrop-win"
   git remote add origin https://github.com/z3x0fly/nitrodrop.git
   git push -u origin main
   ```

   Not: `.gitignore` `vendor/`, `driver/firmware/`, `packaging/out/` gibi üçüncü taraf/derleme çıktılarını zaten dışarıda
   tutuyor. Push'tan önce `git status` temiz olsun; sır (token, şifre) olmadığından emin olmak için bir göz at.
5. Repo ayarları:
   - **Settings → Actions → General:** Actions açık; "Workflow permissions" = Read (iş akışı yalnızca release işinde
     `contents: write` istiyor).
   - **Branch koruması (önerilir):** `main` için ruleset: force-push yasak, silme yasak; PR'lar için 1 onay. SignPath
     bunları isteğe bağlı politika olarak denetleyebiliyor.
   - Açıklama (About): "Free, open-source AirDrop-compatible file sharing for Windows" + web sitesi nitrobyte.com.tr.

## 2. İlk sürüm (imzasız)

1. `ci` iş akışının yeşil olduğunu gör (her push'ta test + Explorer menüsü derlemesi).
2. Etiketle ve gönder:

   ```powershell
   git tag v0.2.0
   git push origin v0.2.0
   ```

3. `release` iş akışı: test → `build-release.ps1 -NoSign -DriverOptional` → artefakt yükleme → (SignPath yoksa imzasız)
   → **taslak** GitHub sürümü. Releases sayfasında taslağı kontrol edip **Publish** de.
   - Sürücü zip'i: GitHub'ın Windows makinesinde WDK yoksa sürücü zip'i oluşmaz (iş akışı uyarı verip devam eder). O
     durumda yerelde `packaging\build-release.ps1` çalıştırıp `NitroDrop-driver-<sürüm>.zip` dosyasını taslağa elle
     ekle; adında "UNSIGNED / test signing only" geçsin.
4. Sürüm notlarında ve README'de "Code signing policy" bağlantısı ile SignPath cümlesi zaten var (şart: ana sayfada ve
   indirme sayfasında "Code signing policy" ifadesi).

## 3. Başvuru formu (https://signpath.org/apply)

Önerilen cevaplar (İngilizce):

- **Project name:** NitroDrop
- **Repository URL:** https://github.com/z3x0fly/nitrodrop
- **Homepage / download page:** https://github.com/z3x0fly/nitrodrop/releases (varsa nitrobyte.com.tr/nitrodrop)
- **License:** MIT (all own code); third-party components: MIT/ISC/BSD-3-Clause/OFL-1.1 — see THIRD-PARTY-NOTICES.md.
- **Description:** "NitroDrop is a free, open-source Windows app that sends and receives files with iPhones, iPads and
  Macs using the AirDrop protocol (interoperability, no Apple code), plus PC-to-PC tab/app sync and iPhone
  notifications over Bluetooth. Built with .NET 10/WPF."
- **What will be signed:** "NitroDrop.exe, NitroDrop.Radio.exe, NitroDrop.SyncHost.exe, nitro-phoneline-probe.exe, our
  own .NET assemblies, the native Explorer menu DLL (NitroDrop.ShellExt.dll) and the MSIX package. Built by GitHub
  Actions (.github/workflows/release.yml) on GitHub-hosted runners, submitted with
  signpath/github-action-submit-signing-request."
- **What will NOT be signed (say this up front):** "Our optional Wi-Fi kernel driver is published separately as an
  unsigned zip (test signing only) and is never part of the signed packages; kernel drivers need Microsoft attestation.
  The app's optional Advanced Setup wizard, shown only when the user opts in, explains and performs the steps needed to
  load that test-signed driver (BitLocker key backup, Secure Boot guidance, enabling test signing, a per-PC
  certificate) with explicit warnings and a one-click rollback. Wintun (WireGuard LLC) is not bundled; it is downloaded
  from wintun.net only after user consent and verified by SHA-256 and signature."
- **Privacy:** link to PRIVACY.md ("no telemetry; no data transfer unless requested by the user").
- **Code signing policy:** link to CODE_SIGNING_POLICY.md.
- **Team:** Emre Can Temur — committer, reviewer, approver (MFA enabled on GitHub and SignPath).
- **Downloads / users:** dürüst sayıyı yaz (yeni proje olduğunu saklama).

## 4. Onaydan sonra (SignPath hesabı açıldığında)

1. SignPath'te **MFA'yı aç**.
2. SignPath projesi: GitHub reposunu "trusted build system" olarak bağla (docs: Trusted build systems → GitHub; SignPath
   GitHub App'ini repoya kurman istenebilir).
3. **Artifact configuration:** `packaging/signpath/artifact-configuration.xml` içeriğini yapıştır (MSIX + portable zip
   içindeki kendi PE dosyalarımız, ürün adı/versiyon kısıtlamalarıyla).
4. **Signing policy:** "release-signing" (SignPath Foundation sertifikası, onay gerekli — approver sensin). Bir CI
   kullanıcısı (submitter) oluşturup API token al.
5. **Sertifika konusu (Publisher):** SignPath'te görünen sertifikanın **Subject** satırını birebir kopyala. Tahmini
   değer `CN=SignPath Foundation, O=SignPath Foundation, L=Lewes, S=Delaware, C=US` — **yer tutucu, doğrula.** MSIX
   manifestindeki Publisher bununla harfi harfine aynı olmazsa MSIX imzalanamaz.
6. GitHub repo → **Settings → Secrets and variables → Actions**:
   - Secret: `SIGNPATH_API_TOKEN` = CI kullanıcısının token'ı
   - Variables: `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG` (ör. `nitrodrop`), `SIGNPATH_SIGNING_POLICY_SLUG`
     (ör. `release-signing`), isteğe bağlı `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`, ve `MSIX_PUBLISHER` (5. adımdaki subject).
7. Yeni etiket (`v0.2.1`) → iş akışı imza isteği gönderir → SignPath'te isteği **onayla** → imzalı MSIX/zip taslak
   sürüme eklenir (sürücü zip'i imzasız etiketli) → kontrol et → Publish.
8. MSIX'i yükleyen kullanıcılar artık `NitroDrop-dev.cer` kurmak zorunda değil; README'deki kurulum talimatını buna
   göre sadeleştiririz.

## Yerelde derleme (değişmedi)

`packaging\build-release.ps1 -Sign` → `CN=Nitrobyte` geliştirme sertifikasıyla imzalı MSIX (yalnızca senin PC'nde
güvenilir). `-NoSign -Publisher "<SignPath subject>"` CI'ın yaptığının aynısı.
