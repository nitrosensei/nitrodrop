# "Telefon" sekmesi — UI entegrasyonu

iPhone bağlantısı (bildirimler / mesajlar / aramalar / müzik) için tüm kod hazır ve derleniyor; uygulamaya
bağlamak için aşağıdaki **küçük düzenlemeler** gerekiyor. Bunlar başka bir ajanın düzenlediği dosyalarda olduğu için
uygulanmadı.

Hazır parçalar:

| Dosya | Görev |
|---|---|
| `app/Nitro.Continuity.Windows/*` | Gerçek çalışma zamanı: eşleştirme, LE bağlantısı (A/B/C), ANCS, AMS, MAP+MNS, PBAP, CallService (PhoneLine / ANCS yedeği), Phone Link çakışma tespiti → `ContinuityRuntime : IContinuityService` |
| `app/AirDrop.App/Services/ContinuityHost.cs` | Kompozisyon: gerçek ya da demo servisini seçer, `PhoneViewModel`'i kurar, arama penceresini ve bildirim toast'larını sağ üstte üst üste dizer (asla odak çalmaz) |
| `app/AirDrop.App/ViewModels/PhoneViewModel.cs` | Sekmenin tüm mantığı (WPF'siz, testli) |
| `app/AirDrop.App/Views/PhonePanel.xaml` | "Telefon" sekmesinin içeriği (UserControl) |
| `app/AirDrop.App/Views/IncomingCallWindow.xaml` | Gelen arama bandı → görüşme mini penceresi |
| `app/AirDrop.App/Views/NotificationToast.xaml` | iPhone bildirim bandı |
| `app/AirDrop.App/ContinuitySnapshots.cs` | Yalnız Debug: ekran dışında PNG üretimi |

## 1. `App.xaml.cs`

Alan:

```csharp
private ContinuityHost? _phone;
```

`_vm = new MainViewModel(...)` satırından **hemen sonra**:

```csharp
_phone = ContinuityHost.Create(demo, (contacts as ContactBookResolver)?.Book, Dispatcher,
                               autoplay: e.Args.Contains("--demo"));
_vm.Phone = _phone.ViewModel;
_phone.ShowPhoneTab = () => { _vm.Tab = MainTab.Phone; _main?.Show(); _main?.Activate(); };
```

(`ContactBookResolver.Book` adı farklıysa, PBAP ile gelen kişilerin yazılacağı `ContactBook` örneğini verin; `null`
verilirse kişi eşitleme yine çalışır ama kalıcı rehbere yazılmaz.)

Snapshot bloğunda, `await Snapshot.RunAsync(...)` satırından sonra (tek satır):

```csharp
await ContinuitySnapshots.RenderAsync(e.Args[snap + 1]);
```

`await _vm.StartAsync();` satırından sonra:

```csharp
await _phone.StartAsync();
```

Çıkışta (`_sync.DisposeAsync()` yanına):

```csharp
if (_phone is not null) await _phone.DisposeAsync();
```

## 2. `MainViewModel.cs`

```csharp
public enum MainTab { Nearby, Devices, Phone, History, Settings }   // Phone eklendi

public PhoneViewModel? Phone { get; set; }
public bool IsPhone { get => Tab == MainTab.Phone; set { if (value) Tab = MainTab.Phone; } }
```

`Tab` setter'ındaki `Raise(...)` listesine `Raise(nameof(IsPhone));` ekleyin. `using AirDrop.App.ViewModels;` zaten
aynı ad alanında.

## 3. `MainWindow.xaml`

Kök elemana ad alanı (yoksa): `xmlns:views="clr-namespace:AirDrop.App.Views"`

Başlık çubuğundaki segmentlere, "Cihazlar" ile "Geçmiş" arasına:

```xml
<RadioButton Style="{StaticResource Segment}" Content="Telefon" GroupName="tabs"
             IsChecked="{Binding IsPhone, Mode=TwoWay}"/>
```

Sayfa ızgarasına (diğer `Grid.Row="1"` sayfalarının yanına):

```xml
<views:PhonePanel Grid.Row="1" DataContext="{Binding Phone}"
                  Visibility="{Binding DataContext.IsPhone, RelativeSource={RelativeSource AncestorType=Window},
                               Converter={StaticResource BoolVis}}"/>
```

Pencere 560 px genişlikte tasarlandı (panel kendi 20 px kenar boşluğunu içerir); 4 segment başlıkta sığmazsa
`Segment` padding'ini 14→11 yapmak yeterli.

## 4. Ayarlar sayfası (isteğe bağlı satırlar)

Ayarlar'da yeni bir "TELEFON" bölümü; satırlar mevcut `Section` / `Row` / `Switch` stilleriyle:

```xml
<TextBlock Text="TELEFON" Style="{StaticResource SectionTitle}"/>
<Border Style="{StaticResource Section}" DataContext="{Binding Phone}">
    <StackPanel>
        <Grid Style="{StaticResource Row}">
            <TextBlock Text="iPhone bildirimlerini göster" VerticalAlignment="Center"/>
            <CheckBox Style="{StaticResource Switch}" HorizontalAlignment="Right" IsChecked="{Binding NotificationToasts}"/>
        </Grid>
        <Border Style="{StaticResource Divider}"/>
        <Grid Style="{StaticResource Row}">
            <TextBlock Text="Gelen aramaları göster" VerticalAlignment="Center"/>
            <CheckBox Style="{StaticResource Switch}" HorizontalAlignment="Right" IsChecked="{Binding CallToasts}"/>
        </Grid>
        <Border Style="{StaticResource Divider}"/>
        <Grid Style="{StaticResource Row}">
            <TextBlock Text="Bildirim önizlemesi (mesaj metni)" VerticalAlignment="Center"/>
            <CheckBox Style="{StaticResource Switch}" HorizontalAlignment="Right" IsChecked="{Binding ShowPreviews}"/>
        </Grid>
    </StackPanel>
</Border>
```

Ayarlar `%LOCALAPPDATA%\airdrop-win\phone-settings.json` dosyasına anında yazılır. Eşleşilen iPhone
`%LOCALAPPDATA%\airdrop-win\phone.json` içinde tutulur.

## 5. Davranış notları

- `--demo`: sahte iPhone "Emre'nin iPhone'u"; 4 sn sonra bir arama çalar, 13 sn sonra bir SMS gelir.
  `--snapshot <dir>`: demo verisiyle, otomatik olay yok.
- Gerçek modda uygulama açılışta **hiçbir cihazla eşleşmez**; yalnızca daha önce kullanıcı "iPhone'u bul → Seç"
  ile seçtiyse o cihaza bağlanır.
- Arama sesi (FullAudio) yalnızca paket kimliğiyle (MSIX / sparse package) ve Phone Link iPhone'u tutmuyorken
  mümkün. Paketsiz çalışmada aramalar ANCS üzerinden yanıtlanır/reddedilir, konuşma iPhone'da sürer; kart bunu ve
  çözümünü gösterir (`CallCapabilityPolicy`).
- Banner pencereleri `ShowActivated=false`, `ShowInTaskbar=false`, `Topmost`; en fazla 3 bildirim + arama.

## 6. Paketleme

FullAudio için paket kimliği (MSIX ya da sparse package) ve manifestte telefon araması yetkisi gerekir
(`phoneCall`; tam ad alanı/biçim bir pakette doğrulanmadı). Ayrıntılar `docs/continuity-plan.md` §9.5.
