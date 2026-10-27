<#
.SYNOPSIS
  NitroDrop USB probe: USB aygitlarini listeler, VID/PID, mevcut surucu (service/INF) ve
  desteklenen (ya da aday) bir Wi-Fi yongasi olup olmadigini raporlar.

.DESCRIPTION
  SADECE OKUR. Surucu, sertifika, INF kurmaz; hicbir aygiti etkinlestirmez/devre disi birakmaz;
  kayit defterine yazmaz. Yonetici yetkisi gerekmez.
  Ayrintilar: docs/usb-plan.md

.PARAMETER All
  Tum USB aygitlarini (hub, HID vb. dahil) gosterir. Varsayilan: sadece "USB\VID_" ile baslayan
  aygitlar ve bilinen Wi-Fi yongalari ozetlenir.

.PARAMETER Json
  Ciktiyi JSON olarak verir (otomasyon icin).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\usb-list.ps1
#>
[CmdletBinding()]
param(
    [switch]$All,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

# --- Bilinen yongalar -------------------------------------------------------------------------
# Tier: Primary = birincil hedef, Backup = yedek, Candidate = degerlendirildi/onerilmiyor.
# MT7612U listesi Linux mt76/mt76x2/usb.c ID tablosunun tamamidir (2026-10-28 master).
# Digerleri bellekten/ikincil kaynaklardan; UNVERIFIED satirlari kaynakta dogrulanmadi.
# Ayni VID altinda hem MediaTek hem Realtek urun olabilir: her zaman tam VID:PID ile eslenir.
$Chips = @(
    @{ Chip = 'MT7612U';  Linux = 'mt76x2u'; Tier = 'Primary';   Ids = @(
        '0E8D:7612', # MediaTek referans (Alfa AWUS036ACM, Comfast CF-WU782AC v2, cogu jenerik)
        '0E8D:7632', # MT7632U
        '0B05:1833', # ASUS USB-AC54 / USB-AC55
        '0B05:17EB', # ASUS USB-AC55
        '0B05:180B', # ASUS USB-N53 B1
        '0846:9053', # Netgear A6210
        '045E:02E6', # Microsoft Xbox Wireless Adapter (WiFi kismi degil; mt76x2u tablosunda)
        '045E:02FE', # Microsoft Xbox Wireless Adapter v2
        '057C:8503', # AVM FRITZ!WLAN AC860
        '7392:B711', # Edimax EW-7722UAC
        '2C4E:0103', # Mercury UD13
        '056E:400A', # ELECOM WDC-867SU3S
        '0846:9014', # Netgear WNDA3100v3
        '0471:2126', # LiteOn WN4516R
        '0471:7600', # LiteOn WN4519R
        '2357:0137'  # TP-Link TL-WDN6200
    ) }
    @{ Chip = 'MT7610U';  Linux = 'mt76x0u'; Tier = 'Backup';    Ids = @(
        '0E8D:7610', '0E8D:7630', '0E8D:7650', # MediaTek referans (Alfa AWUS036ACHM = 0E8D:7610)
        '148F:7610', # Ralink/MediaTek
        '0B05:17D1', # ASUS USB-AC51
        '0B05:17DB', # ASUS USB-AC50
        '2357:0105', # TP-Link Archer T1U
        '7392:A711', # Edimax EW-7711MAC
        '7392:B711', # Edimax (bkz. ustte; tabloda iki surucude de gecebilir) UNVERIFIED
        '13B1:003E', # Linksys AE6000
        '2019:AB31', # Planex GW-450D
        '0586:3425', # ZyXEL NWD6505
        '0DF6:0075', # Sitecom WLA-3100
        '2001:3D02', # D-Link DWA-171 rev B1
        '20F4:806B', # TRENDnet TEW-806UBH
        '293C:5702', # Comcast Xfinity KXW02AAA
        '057C:8502'  # AVM FRITZ!WLAN USB Stick AC 430
    ) }
    @{ Chip = 'MT7921AU'; Linux = 'mt7921u'; Tier = 'Candidate'; Ids = @(
        '0E8D:7961', # MediaTek referans (Alfa AWUS036AXML, Comfast CF-951AX/952AX bazi revizyonlar)
        '3574:6211', # Comfast CF-952AX
        '0846:9060', # Netgear A8000
        '35BC:0107'  # TP-Link TXE50UH UNVERIFIED
    ) }
    @{ Chip = 'RTL8812AU'; Linux = 'rtw88_8812au / rtl8812au(GPL)'; Tier = 'Candidate'; Ids = @(
        '0BDA:8812', '0BDA:881A', '0BDA:881B', '0BDA:881C', # Realtek referans (Alfa AWUS036ACH)
        '2357:0101', # TP-Link Archer T4U v1
        '2357:0103', # TP-Link Archer T4UH v1
        '2357:010D', # TP-Link Archer T4U v2 UNVERIFIED
        '2357:010E', '2357:010F', # TP-Link T4UH v2 UNVERIFIED
        '0846:9051', # Netgear A6200 v2
        '7392:A822', # Edimax EW-7822UAC
        '0B05:17D2', # ASUS USB-AC56
        '2001:3313'  # D-Link DWA-182 rev C1 UNVERIFIED
    ) }
    @{ Chip = 'RTL8811AU/8821AU'; Linux = 'rtw88_8821au'; Tier = 'Candidate'; Ids = @(
        '0BDA:0811', '0BDA:0821', '0BDA:8822', '0BDA:A811', # Realtek referans
        '2357:011E', '2357:011F', # TP-Link Archer T2U Nano / T2U v3 UNVERIFIED
        '2357:0120'  # TP-Link Archer T2U Plus UNVERIFIED
    ) }
    @{ Chip = 'AIC8800'; Linux = '(vendor, out-of-tree)'; Tier = 'Unsupported'; Ids = @(
        'A69C:8800', 'A69C:88DC', 'A69C:8D80', '368B:88DF', '2604:0013' # Tenda U2/U9 vb. UNVERIFIED
    ) }
)

$IdIndex = @{}
foreach ($c in $Chips) { foreach ($id in $c.Ids) { if (-not $IdIndex.ContainsKey($id)) { $IdIndex[$id] = $c } } }

# --- Aygitlari oku (salt okunur CIM/PnP sorgulari) ---------------------------------------------
$entities = Get-CimInstance -ClassName Win32_PnPEntity -Filter "PNPDeviceID LIKE 'USB\\%'" |
    Where-Object { $_.PNPDeviceID -match '^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}' }

function Get-Prop([string]$InstanceId, [string]$Key) {
    try {
        $p = Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName $Key -ErrorAction Stop
        return $p.Data
    } catch { return $null }
}

$rows = foreach ($e in $entities) {
    $m = [regex]::Match($e.PNPDeviceID, '^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(&MI_([0-9A-F]{2}))?\\(.*)$', 'IgnoreCase')
    $vid = $m.Groups[1].Value.ToUpper(); $pid_ = $m.Groups[2].Value.ToUpper()
    $mi = $m.Groups[4].Value
    $key = "${vid}:${pid_}"
    $chip = $IdIndex[$key]
    if (-not $All -and -not $chip -and $e.PNPClass -in @('USB', 'HIDClass', 'Keyboard', 'Mouse', 'Bluetooth')) { continue }

    [pscustomobject]@{
        VidPid     = $key
        Interface  = $(if ($mi) { "MI_$mi" } else { '' })
        Name       = $e.Name
        Class      = $e.PNPClass
        Service    = $e.Service
        Inf        = Get-Prop $e.PNPDeviceID 'DEVPKEY_Device_DriverInfPath'
        Provider   = Get-Prop $e.PNPDeviceID 'DEVPKEY_Device_DriverProvider'
        DriverVer  = Get-Prop $e.PNPDeviceID 'DEVPKEY_Device_DriverVersion'
        Status     = $e.Status
        Chip       = $(if ($chip) { $chip.Chip } else { '' })
        Tier       = $(if ($chip) { $chip.Tier } else { '' })
        InstanceId = $e.PNPDeviceID
    }
}

$rows = $rows | Sort-Object @{e = { if ($_.Chip) { 0 } else { 1 } } }, VidPid, Interface

if ($Json) {
    $rows | ConvertTo-Json -Depth 3
    return
}

Write-Host "NitroDrop USB probe  (salt okunur; hicbir sey kurulmaz/degistirilmez)" -ForegroundColor Cyan
Write-Host ("Makine: {0}   Tarih: {1:yyyy-MM-dd HH:mm}" -f $env:COMPUTERNAME, (Get-Date))
try {
    $sb = Confirm-SecureBootUEFI -ErrorAction Stop
    Write-Host "Secure Boot: $(if ($sb) { 'ACIK' } else { 'KAPALI' })"
} catch {
    # Yonetici olmadan: kayit defterinden oku (salt okunur)
    $v = $null
    try { $v = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' -ErrorAction Stop).UEFISecureBootEnabled } catch {}
    if ($null -ne $v) { Write-Host "Secure Boot: $(if ($v -eq 1) { 'ACIK' } else { 'KAPALI' }) (kayit defteri)" }
    else { Write-Host "Secure Boot: okunamadi" }
}
try {
    $dg = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction Stop
    $hvci = $dg.SecurityServicesRunning -contains 2
    Write-Host "Memory Integrity (HVCI) calisiyor: $(if ($hvci) { 'EVET' } else { 'HAYIR' })"
} catch { Write-Host "Memory Integrity (HVCI): okunamadi" }
Write-Host ""

$rows | Format-Table -AutoSize -Wrap VidPid, Interface, Name, Class, Service, Inf, Chip, Tier | Out-String -Width 220 | Write-Host

$found = $rows | Where-Object { $_.Chip -and ($_.Interface -eq '' -or $_.Interface -eq 'MI_00') }
if ($found) {
    Write-Host "Bulunan Wi-Fi yongalari:" -ForegroundColor Green
    foreach ($f in $found) {
        $bind = switch -Regex ($f.Service) {
            '^WinUSB$'   { 'WinUSB bagli -> NitroDrop kullanabilir'; break }
            '^$'         { 'surucu yok -> WinUSB baglanabilir'; break }
            default      { "baska surucu bagli ($($f.Service)) -> WinUSB'ye gecis gerekir (bkz. docs/usb-plan.md)" }
        }
        Write-Host ("  {0}  {1,-10} [{2}]  {3}" -f $f.VidPid, $f.Chip, $f.Tier, $f.Name)
        Write-Host ("      durum: {0}   INF: {1}   instance: {2}" -f $bind, $f.Inf, $f.InstanceId)
        if ($f.Tier -eq 'Unsupported') { Write-Host "      not: bu yonga NitroDrop USB planinda desteklenmiyor (internet icin kullanilmaya devam)" }
    }
} else {
    Write-Host "Desteklenen bir USB Wi-Fi yongasi bulunamadi. Onerilen: MT7612U (ornegin Alfa AWUS036ACM)." -ForegroundColor Yellow
}
